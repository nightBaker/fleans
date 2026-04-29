"""
Locust port of tests/load/scripts/parallel.js — Scenario 2 (parallel branching).

Each task POSTs /Workflow/start { WorkflowId: "load-parallel" }. The fixture is a
3-branch fork/join (branchA/branchB/branchC). As with k6 parallel.js, this only
measures the start-call latency; fork/join completion is asynchronous server-side
and not captured here.
"""

from locust import HttpUser, task, constant


class ParallelUser(HttpUser):
    wait_time = constant(0.1)

    @task
    def start_parallel_workflow(self):
        with self.client.post(
            "/Workflow/start",
            json={"WorkflowId": "load-parallel"},
            name="workflow_start",
            catch_response=True,
        ) as response:
            if response.status_code != 200:
                response.failure(f"status {response.status_code}: {response.text[:200]}")
