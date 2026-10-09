# Manual Test Plan: AWS MSK IAM authentication (#682)

**STATUS: NEEDS-RERUN**

> **Human-only — requires AWS account and an Amazon MSK cluster (~$0.50/h cost).**
> Unit tests cover wiring; only a real MSK cluster validates the SigV4 token lifecycle.

## Prerequisites

- AWS account with permission to create/use MSK clusters.
- IAM role (instance profile, EKS Pod Identity, or ECS task role) attached to the silo host with the following policy actions on the MSK cluster:
  ```json
  {
    "Effect": "Allow",
    "Action": [
      "kafka-cluster:Connect",
      "kafka-cluster:DescribeCluster",
      "kafka-cluster:WriteData",
      "kafka-cluster:ReadData",
      "kafka-cluster:DescribeTopic",
      "kafka-cluster:CreateTopic"
    ],
    "Resource": "arn:aws:kafka:<region>:<account>:cluster/<cluster-name>/*"
  }
  ```
- MSK cluster created with **IAM authentication enabled** (Broker type: `kafka.m5.large` or larger).
- Bootstrap broker endpoint (IAM port `:9098`), e.g. `b-1.cluster.kafka.eu-west-1.amazonaws.com:9098`.

---

## Test A — Successful IAM authentication and workflow completion

**Setup:**
1. Set environment variables on the silo host:
   ```
   FLEANS_STREAMING_PROVIDER=Kafka
   AWS_REGION=<your-region>
   Fleans__Streaming__Kafka__Brokers=<broker>:9098
   ```
2. Register the MSK IAM stream provider in silo startup (replacing the default `AddKafkaStreams`):
   ```csharp
   siloBuilder.AddKafkaStreamingWithMskIam("kafka", configuration);
   ```
3. Start `dotnet run --project Fleans.Aspire`.

**Steps:**
1. Run a BPMN workflow that passes through a Script Task (exercises Kafka stream internally).
2. Observe silo startup logs — EventId 11200 (`LogTokenRefreshFailed`) should NOT appear.
3. Verify the workflow completes.

**Pass:** Workflow completes; no EventId 11200 in logs; no SASL handshake errors.

---

## Test B — Wrong port produces clear error (port `:9092` instead of `:9098`)

**Setup:** Same as Test A but set `Fleans__Streaming__Kafka__Brokers=<broker>:9092`.

**Steps:**
1. Start the stack. Observe logs within 30 seconds.

**Pass:** SASL handshake failure appears in logs (generic "could not get OAUTH token" or similar broker rejection). This validates that `:9092` (plaintext) does not silently succeed with IAM auth — the error directs operators to check the port.

---

## Test C — Missing IAM permissions produces EventId 11200

**Setup:** Attach an IAM role with NO `kafka-cluster:*` permissions to the silo host.

**Steps:**
1. Start the stack. Observe logs within 30 seconds.

**Pass:** EventId 11200 (`MSK IAM token refresh failed`) appears at ERROR level. The `Region` structured field reflects the configured region. The workflow may eventually fail or timeout, but the log signal is clear.

---

## Test D — Missing region (no param, no env var) throws at startup

**Setup:** Remove `AWS_REGION` and `AWS_DEFAULT_REGION` from env. Call `AddKafkaStreamingWithMskIam(name, config)` with no `region` param.

**Steps:**
1. Attempt to start the silo.

**Pass:** Silo startup throws `InvalidOperationException` with message "AWS region must be supplied via the `region` parameter or the AWS_REGION env var." before any broker connection is attempted.

---

## Test E — Explicit region param takes precedence over env var

**Setup:** Set `AWS_REGION=eu-west-1` but pass `region: "us-east-1"` to `AddKafkaStreamingWithMskIam`.

**Steps:**
1. Check the IAM token's audience/region in the silo DEBUG logs (or via AWS CloudTrail).

**Pass:** The IAM token is signed for `us-east-1`, not `eu-west-1`.
