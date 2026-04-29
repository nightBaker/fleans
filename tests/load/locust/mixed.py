"""
Locust port of tests/load/scripts/mixed.js — Scenario 4 (mixed workload).

Three User classes weighted 40/30/30 mirror the k6 mixed.js scenario splits:
  - LinearUser    (40%): POST /Workflow/start  WorkflowId=load-linear
  - ParallelUser  (30%): POST /Workflow/start  WorkflowId=load-parallel
  - EventsUser    (30%): full 3-phase event-driven loop

When run via Azure Load Testing, set LOCUST_USERS to the *total* concurrent
users (Locust distributes across the weighted classes automatically).
"""

import os
import time
import uuid

from locust import HttpUser, task, constant


def _int_env(name, default):
    try:
        v = int(os.environ.get(name, "") or default)
        return v if v > 0 else default
    except ValueError:
        return default


POLL_INTERVAL_MS         = _int_env("K6_POLL_INTERVAL_MS",         100)
POLL_BACKOFF_CAP_MS      = _int_env("K6_POLL_BACKOFF_CAP_MS",      500)
POLL_TOTAL_BUDGET_MS     = _int_env("K6_POLL_TOTAL_BUDGET_MS",    3000)
MESSAGE_RETRY_BUDGET_MS  = _int_env("K6_MESSAGE_RETRY_BUDGET_MS", 1000)
MESSAGE_RETRY_INTERVAL_MS= _int_env("K6_MESSAGE_RETRY_INTERVAL_MS", 150)


class LinearUser(HttpUser):
    weight = 40
    wait_time = constant(0.1)

    @task
    def start_linear(self):
        with self.client.post(
            "/Workflow/start",
            json={"WorkflowId": "load-linear"},
            name="linear:workflow_start",
            catch_response=True,
        ) as r:
            if r.status_code != 200:
                r.failure(f"status {r.status_code}: {r.text[:200]}")


class ParallelUser(HttpUser):
    weight = 30
    wait_time = constant(0.1)

    @task
    def start_parallel(self):
        with self.client.post(
            "/Workflow/start",
            json={"WorkflowId": "load-parallel"},
            name="parallel:workflow_start",
            catch_response=True,
        ) as r:
            if r.status_code != 200:
                r.failure(f"status {r.status_code}: {r.text[:200]}")


class EventsUser(HttpUser):
    weight = 30
    wait_time = constant(0.1)

    @task
    def event_driven(self):
        request_id = str(uuid.uuid4())

        with self.client.post(
            "/Workflow/start",
            json={
                "WorkflowId": "load-events",
                "Variables": {"requestId": request_id},
            },
            name="events:workflow_start",
            catch_response=True,
        ) as r:
            if r.status_code != 200:
                r.failure(f"status {r.status_code}: {r.text[:200]}")
                return
            try:
                instance_id = r.json()["workflowInstanceId"]
            except Exception:
                r.failure("missing workflowInstanceId in response")
                return

        deadline = time.monotonic() + (POLL_TOTAL_BUDGET_MS / 1000.0)
        wait_ms = POLL_INTERVAL_MS
        caught = False
        while time.monotonic() < deadline:
            with self.client.get(
                f"/Workflow/instances/{instance_id}/state",
                name="events:poll_state",
                catch_response=True,
            ) as pr:
                if pr.status_code == 200:
                    try:
                        active = pr.json().get("activeActivityIds") or []
                    except Exception:
                        active = []
                    if "waitMessage" in active:
                        caught = True
                        break
                else:
                    pr.failure(f"poll status {pr.status_code}")

            sleep_s = min(wait_ms, max(0, int((deadline - time.monotonic()) * 1000))) / 1000.0
            if sleep_s > 0:
                time.sleep(sleep_s)
            wait_ms = min(wait_ms * 3 // 2, POLL_BACKOFF_CAP_MS)

        if not caught:
            self.environment.events.request.fire(
                request_type="STALL",
                name="events:poll_stall",
                response_time=POLL_TOTAL_BUDGET_MS,
                response_length=0,
                exception=Exception("timeout waiting for waitMessage"),
                context={},
            )
            return

        msg_deadline = time.monotonic() + (MESSAGE_RETRY_BUDGET_MS / 1000.0)
        while True:
            with self.client.post(
                "/Workflow/message",
                json={
                    "MessageName": "loadMessage",
                    "CorrelationKey": request_id,
                    "Variables": {},
                },
                name="events:send_message",
                catch_response=True,
            ) as mr:
                if mr.status_code == 200:
                    break
                if mr.status_code != 404:
                    mr.failure(f"message status {mr.status_code}: {mr.text[:200]}")
                    return
                if time.monotonic() >= msg_deadline:
                    mr.failure("404 retry budget exhausted")
                    return
            time.sleep(MESSAGE_RETRY_INTERVAL_MS / 1000.0)
