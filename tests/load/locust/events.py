"""
Locust port of tests/load/scripts/events.js — Scenario 3 (event-driven).

Each iteration:
  1. POST /Workflow/start { WorkflowId: "load-events", Variables: {requestId: <uuid>} }
  2. Poll GET /Workflow/instances/{id}/state until activeActivityIds includes
     'waitMessage'. Backoff capped at K6_POLL_BACKOFF_CAP_MS, total budget
     K6_POLL_TOTAL_BUDGET_MS (k6 names kept for parity).
  3. POST /Workflow/message with the correlation key, retried on 404.

Three named request labels are emitted ("workflow_start", "poll_state",
"send_message") so Azure Load Testing can show per-phase latency separately.
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

FIXTURE = {
    "process_id":       "load-events",
    "catch_id":         "waitMessage",
    "message_name":     "loadMessage",
    "correlation_var":  "requestId",
}


class EventsUser(HttpUser):
    wait_time = constant(0.1)

    @task
    def event_driven_workflow(self):
        request_id = str(uuid.uuid4())

        # Phase 1 — start
        with self.client.post(
            "/Workflow/start",
            json={
                "WorkflowId": FIXTURE["process_id"],
                "Variables": {FIXTURE["correlation_var"]: request_id},
            },
            name="workflow_start",
            catch_response=True,
        ) as r:
            if r.status_code != 200:
                r.failure(f"start status {r.status_code}: {r.text[:200]}")
                return
            try:
                instance_id = r.json()["workflowInstanceId"]
            except Exception:
                r.failure("missing workflowInstanceId in response")
                return

        # Phase 2 — poll until waitMessage shows in activeActivityIds
        deadline = time.monotonic() + (POLL_TOTAL_BUDGET_MS / 1000.0)
        wait_ms = POLL_INTERVAL_MS
        caught = False
        while time.monotonic() < deadline:
            with self.client.get(
                f"/Workflow/instances/{instance_id}/state",
                name="poll_state",
                catch_response=True,
            ) as pr:
                if pr.status_code == 200:
                    try:
                        active = pr.json().get("activeActivityIds") or []
                    except Exception:
                        active = []
                    if FIXTURE["catch_id"] in active:
                        caught = True
                        break
                else:
                    pr.failure(f"poll status {pr.status_code}")

            sleep_s = min(wait_ms, max(0, int((deadline - time.monotonic()) * 1000))) / 1000.0
            if sleep_s > 0:
                time.sleep(sleep_s)
            wait_ms = min(wait_ms * 3 // 2, POLL_BACKOFF_CAP_MS)

        if not caught:
            # Mirror k6's poll_stalls metric — emit a synthetic failed request so
            # ALT shows it in the dashboard alongside HTTP latency.
            self.environment.events.request.fire(
                request_type="STALL",
                name="poll_stall",
                response_time=POLL_TOTAL_BUDGET_MS,
                response_length=0,
                exception=Exception("timeout waiting for waitMessage"),
                context={},
            )
            return

        # Phase 3 — send message with budgeted retry on 404
        msg_deadline = time.monotonic() + (MESSAGE_RETRY_BUDGET_MS / 1000.0)
        attempts = 0
        while True:
            with self.client.post(
                "/Workflow/message",
                json={
                    "MessageName": FIXTURE["message_name"],
                    "CorrelationKey": request_id,
                    "Variables": {},
                },
                name="send_message",
                catch_response=True,
            ) as mr:
                attempts += 1
                if mr.status_code == 200:
                    break
                if mr.status_code != 404:
                    mr.failure(f"message status {mr.status_code}: {mr.text[:200]}")
                    return
                if time.monotonic() >= msg_deadline:
                    mr.failure("404 retry budget exhausted")
                    return
            time.sleep(MESSAGE_RETRY_INTERVAL_MS / 1000.0)
