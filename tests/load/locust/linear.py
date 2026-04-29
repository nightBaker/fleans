"""
Locust port of tests/load/scripts/linear.js — Scenario 1 (linear throughput).

Each task issues one POST /Workflow/start { WorkflowId: "load-linear" } against
the Fleans API. Mirrors the k6 script's measurement surface:
  - Latency / throughput / error rate are emitted by Locust under the
    request name "workflow_start".

Usage (locally, sanity check):
  locust -f linear.py --host https://<fleans-public-url> --headless \\
         -u 5 -r 1 --run-time 30s

In Azure Load Testing the Locust test-type provides --host, -u, -r, --run-time
through the test configuration. Set the target via the test's env block:
  LOCUST_HOST=https://<fleans-public-url>
"""

from locust import HttpUser, task, between, constant


class LinearUser(HttpUser):
    # No artificial think time — match k6's tight loop. The k6 linear.js has
    # `sleep(0.1)` between iterations; mirror that to keep apples-to-apples.
    wait_time = constant(0.1)

    @task
    def start_linear_workflow(self):
        with self.client.post(
            "/Workflow/start",
            json={"WorkflowId": "load-linear"},
            name="workflow_start",
            catch_response=True,
        ) as response:
            if response.status_code != 200:
                response.failure(f"status {response.status_code}: {response.text[:200]}")
