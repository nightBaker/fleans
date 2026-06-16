# Manual Test Plan: Kafka SASL / SecurityProtocol config (#680)

**STATUS: NEEDS-RERUN**

## Prerequisites

- Docker with a Kafka image that supports SASL (e.g. `confluentinc/cp-kafka:7.x` or `bitnami/kafka:3.x`)
- Local Fleans stack started via `dotnet run --project Fleans.Aspire` with `FLEANS_STREAMING_PROVIDER=Kafka`

---

## Test 1 — Plaintext (baseline, backward-compat)

**Setup:** Start Kafka without auth. Set `Fleans:Streaming:Kafka:Brokers=localhost:9092`.

**Steps:**
1. Start `dotnet run --project Fleans.Aspire` with `FLEANS_STREAMING_PROVIDER=Kafka`.
2. Run a hello-world workflow that passes through a Script Task (uses Kafka stream internally).
3. Verify the workflow completes.

**Pass:** Workflow completes; no Kafka errors in logs; no new `SecurityProtocol` setting needed.

---

## Test 2 — SASL/PLAIN

**Setup:** Start Kafka with SASL_PLAINTEXT + PLAIN mechanism.

Example Docker Compose snippet:
```yaml
environment:
  KAFKA_LISTENER_SECURITY_PROTOCOL_MAP: SASL_PLAINTEXT:SASL_PLAINTEXT
  KAFKA_SASL_ENABLED_MECHANISMS: PLAIN
  KAFKA_SASL_JAAS_CONFIG: 'org.apache.kafka.common.security.plain.PlainLoginModule required username="fleans" password="secret" user_fleans="secret";'
```

**Fleans config:**
```json
{
  "Fleans": {
    "Streaming": {
      "Kafka": {
        "SecurityProtocol": "SaslPlaintext",
        "SaslMechanism": "Plain",
        "SaslUsername": "fleans",
        "SaslPassword": "secret"
      }
    }
  }
}
```

**Steps:**
1. Start Fleans with the above config.
2. Run a hello-world workflow.
3. Verify the workflow completes.

**Pass:** Workflow completes; silo log does not contain auth errors.

---

## Test 3 — SASL/SCRAM-SHA-512

**Setup:** Start Kafka with SASL_SSL + SCRAM-SHA-512.

**Fleans config:**
```json
{
  "Fleans": {
    "Streaming": {
      "Kafka": {
        "SecurityProtocol": "SaslSsl",
        "SaslMechanism": "ScramSha512",
        "SaslUsername": "fleans",
        "SaslPassword": "secret"
      }
    }
  }
}
```

**Steps:**
1. Start Fleans with the above config.
2. Run a hello-world workflow.
3. Verify the workflow completes.

**Pass:** Workflow completes; no auth errors.

---

## Test 4 — Misconfiguration is rejected at startup

**Setup:** Set `SecurityProtocol=SaslPlaintext` but omit `SaslMechanism`.

**Steps:**
1. Start Fleans. Expect the silo to fail fast with an `InvalidOperationException` mentioning "SaslMechanism is required".
2. Verify the process does not reach an unhealthy running state.

**Pass:** Silo startup fails with a clear `InvalidOperationException`. No silent misconfiguration.

---

## Test 5 — Default plaintext is unaffected by the new properties

**Setup:** No security properties set (just `Brokers`). Same as before #680.

**Steps:**
1. Start Fleans. No new config needed.
2. Verify startup succeeds and workflow runs normally.

**Pass:** Identical behavior to pre-#680 for plaintext Kafka deployments.
