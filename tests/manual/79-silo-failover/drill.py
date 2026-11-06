#!/usr/bin/env python3
"""Silo-failover drill (#723): kill a Core silo while instances wait on a user task and a timer.

Usage (from the repo root, with tests/load/docker-compose.yml up — see test-plan.md):

    python3 -I tests/manual/79-silo-failover/drill.py [--instances 150] [--url http://localhost]
                                                       [--victim fleans-load-fleans-core-1] [--stop]
                                                       [--kill-after 88]

Prints a report: how long completions were refused after the signal (the failover window), whether
every instance completed exactly once, and the lag of the timers that fired after the kill.
--stop sends `docker stop -t 60` (SIGTERM, graceful shutdown) instead of `docker kill` (SIGKILL).
"""
import argparse
import concurrent.futures
import json
import pathlib
import re
import statistics
import subprocess
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta

FIXTURE = pathlib.Path(__file__).with_name("silo-failover.bpmn")
PROCESS = "silo-failover"
TIMER = timedelta(seconds=90)
slow_calls = []  # transport errors and timeouts, reported at the end


def call(url, method="GET", body=None, timeout=30):
    """One request; a 429 (rate limit or load shedding) is retried after a pause."""
    data = json.dumps(body).encode() if body is not None else None
    for _ in range(100):
        request = urllib.request.Request(url, data=data, method=method,
                                         headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                text = response.read().decode()
                return json.loads(text) if text else None
        except urllib.error.HTTPError as error:
            if error.code != 429:
                raise
            time.sleep(0.5)
    raise SystemExit(f"{method} {url}: still 429 after 100 tries")


def field(obj, name):
    """The API serializes camelCase; accept either casing."""
    return obj.get(name[0].lower() + name[1:], obj.get(name))


def parse_time(value):
    """ISO 8601 from .NET (1-7 fractional digits); Python 3.9's parser takes exactly 3 or 6."""
    if not value:
        return None
    value = re.sub(r"\.(\d+)", lambda m: "." + m.group(1)[:6].ljust(6, "0"), value.replace("Z", "+00:00"))
    return datetime.fromisoformat(value)


def state(base, instance_id):
    return call(f"{base}/Instances/{instance_id}/state")


def wait_for_task(base, instance_id, deadline):
    while time.time() < deadline:
        snapshot = state(base, instance_id)
        for activity in field(snapshot, "ActiveActivities") or []:
            if field(activity, "ActivityId") == "approve":
                return field(activity, "ActivityInstanceId")
        time.sleep(0.2)
    raise SystemExit(f"{instance_id}: the user task never became active")


def complete_until_accepted(base, task_id, killed_at, give_up_at):
    """Claims and completes the task, retrying until the API accepts both.

    Returns (seconds after the kill, attempts, seconds from the first attempt to acceptance)."""
    first = time.time()
    attempts = 0
    claimed = False
    while True:
        attempts += 1
        try:
            if not claimed:
                # A repeated claim by the same user succeeds, so a lost reply needs no special case.
                call(f"{base}/UserTasks/{task_id}/claim", "POST", {"UserId": "drill"})
                claimed = True
            call(f"{base}/UserTasks/{task_id}/complete", "POST",
                 {"UserId": "drill", "Variables": {"approved": True}})
            return time.time() - killed_at, attempts, time.time() - first
        except urllib.error.HTTPError as error:
            # 404 or 409 on a retry: an earlier completion whose reply was lost did land (the
            # task row is gone, or the activity is no longer active). The final check below
            # confirms that each task completed exactly once.
            if attempts > 1 and claimed and error.code in (404, 409):
                return time.time() - killed_at, attempts, time.time() - first
            if error.code < 500 or time.time() > give_up_at:
                raise
        except OSError as error:  # URLError, connection reset, socket timeout
            slow_calls.append(f"{task_id}: {error!r}")
            if time.time() > give_up_at:
                raise
        time.sleep(0.25)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://localhost")
    parser.add_argument("--instances", type=int, default=150)
    parser.add_argument("--victim", default="fleans-load-fleans-core-1")
    parser.add_argument("--stop", action="store_true", help="docker stop instead of docker kill")
    parser.add_argument("--kill-after", type=float, default=0,
                        help="seconds after the first start to send the signal; 88 makes the timers "
                             "fall due while the silo is being declared dead")
    args = parser.parse_args()
    base = args.url.rstrip("/")

    call(f"{base}/Definitions/deploy", "POST", {"BpmnXml": FIXTURE.read_text()})
    first_start = time.time()
    with concurrent.futures.ThreadPoolExecutor(20) as pool:
        ids = list(pool.map(
            lambda _: field(call(f"{base}/Execution/start", "POST", {"WorkflowId": PROCESS}),
                            "WorkflowInstanceId"),
            range(args.instances)))
        deadline = time.time() + 60
        tasks = list(pool.map(lambda i: wait_for_task(base, i, deadline), ids))
    print(f"Started {len(ids)} instances; every one waits on 'approve' and a {TIMER.seconds} s timer.")

    time.sleep(max(0.0, first_start + args.kill_after - time.time()))
    # docker stop waits 10 s before SIGKILL by default, less than the silo's 30 s shutdown
    # timeout; 60 s matches the Helm chart's terminationGracePeriodSeconds.
    command = ["docker", "stop", "-t", "60", args.victim] if args.stop else ["docker", "kill", args.victim]
    # In the background: with --stop the completions below run while the silo shuts down.
    killed_at = time.time()
    signal = subprocess.Popen(command, stdout=subprocess.DEVNULL)
    print(f"{' '.join(command)} at {datetime.now().isoformat(timespec='seconds')}")

    give_up_at = killed_at + 180
    with concurrent.futures.ThreadPoolExecutor(50) as pool:
        accepted = list(pool.map(lambda t: complete_until_accepted(base, t, killed_at, give_up_at), tasks))
    if signal.wait() != 0:
        raise SystemExit(f"{' '.join(command)} failed")
    print(f"The container was gone {time.time() - killed_at:.1f} s after the signal (or earlier).")
    delays = sorted(after_kill for after_kill, _, _ in accepted)
    retried = sorted(took for _, attempts, took in accepted if attempts > 1)
    direct = sorted(took for _, attempts, took in accepted if attempts == 1)
    print(f"Claims and completions accepted: {len(accepted)}/{len(tasks)}, the last "
          f"{delays[-1]:.1f} s after the signal.")
    for label, group in (("first try", direct), ("after retries", retried)):
        if group:
            print(f"  {label}: {len(group)}, claim + complete took median "
                  f"{statistics.median(group):.1f} s, max {group[-1]:.1f} s")

    finish_by = time.time() + TIMER.total_seconds() + 180
    results = {}
    pending = set(ids)
    while pending and time.time() < finish_by:
        for instance_id in list(pending):
            try:
                snapshot = state(base, instance_id)
            except OSError as error:
                slow_calls.append(f"state {instance_id}: {error!r}")
                continue
            if field(snapshot, "IsCompleted") or field(snapshot, "IsFailed"):
                results[instance_id] = snapshot
                pending.discard(instance_id)
        if pending:
            time.sleep(2)

    failed = [i for i, s in results.items() if field(s, "IsFailed")]
    duplicates = [i for i, s in results.items()
                  if sorted(field(a, "ActivityId") for a in field(s, "CompletedActivities")
                            if field(a, "ActivityId") in ("approve", "wait")) != ["approve", "wait"]]
    lags = []
    for snapshot in results.values():
        for activity in field(snapshot, "CompletedActivities"):
            if field(activity, "ActivityId") == "wait":
                created = parse_time(field(activity, "CreatedAt"))
                completed = parse_time(field(activity, "CompletedAt"))
                if created and completed:
                    lags.append((completed - created - TIMER).total_seconds())
    lags.sort()

    print(f"Completed: {len(results) - len(failed)}/{len(ids)}, failed: {len(failed)}, "
          f"not finished: {len(pending)}, task or timer not completed exactly once: {len(duplicates)}")
    if lags:
        late = sum(1 for lag in lags if lag > 5)
        print(f"Timer lag (fired minus due): median {statistics.median(lags):.1f} s, "
              f"max {lags[-1]:.1f} s; {late} of {len(lags)} fired more than 5 s late")
    print(f"Transport errors or timeouts: {len(slow_calls)}")
    for line in slow_calls[:5]:
        print(f"  {line}")
    for label, items in (("failed", failed), ("not finished", sorted(pending)), ("not exactly once", duplicates)):
        for instance_id in items[:5]:
            print(f"  {label}: {instance_id}")
    if failed or pending or duplicates:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
