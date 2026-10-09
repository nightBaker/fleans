# Manual Test Plan: Schema Registry dual-encoding mechanism (#700)

**STATUS: NEEDS-RERUN**

> **Requires a running Confluent Schema Registry** (local Docker or managed).
> Unit tests cover wiring and fanout logic; only a live SR validates round-trip encoding.

## Prerequisites

- A Kafka cluster (local Docker or remote).
- A Confluent Schema Registry accessible from the silo host.
- An `IExternalEventEncoder` implementation installed — use a test stub (see Test A setup) or a
  real Avro/Protobuf framing package when available (#685B / #685C).

Example SR + Kafka via Docker Compose:

```yaml
services:
  zookeeper:
    image: confluentinc/cp-zookeeper:7.6.0
    environment: { ZOOKEEPER_CLIENT_PORT: 2181 }
  kafka:
    image: confluentinc/cp-kafka:7.6.0
    depends_on: [zookeeper]
    environment:
      KAFKA_ZOOKEEPER_CONNECT: zookeeper:2181
      KAFKA_ADVERTISED_LISTENERS: PLAINTEXT://localhost:9092
  schema-registry:
    image: confluentinc/cp-schema-registry:7.6.0
    depends_on: [kafka]
    ports: ["8081:8081"]
    environment:
      SCHEMA_REGISTRY_HOST_NAME: schema-registry
      SCHEMA_REGISTRY_KAFKASTORE_BOOTSTRAP_SERVERS: kafka:9092
```

---

## Test A — Fanout disabled when no encoder is registered

**Setup:**
1. Start Fleans with Kafka streaming (`FLEANS_STREAMING_PROVIDER=Kafka`).
2. Do NOT register `IExternalEventEncoder` in DI and do NOT call `AddKafkaSchemaRegistry`.

**Steps:**
1. Run a BPMN workflow that passes through a Script Task.
2. Observe Kafka topics — only `fleans-0` … `fleans-N` topics should exist.

**Pass:** Workflow completes normally; no `fleans-N-events` topics exist; EventId 11107/11108 do NOT appear in logs.

---

## Test B — Fanout produces to both topics when encoder is registered

**Setup:**
1. Start Fleans with Kafka streaming and register a stub `IExternalEventEncoder` that returns `new byte[]{0x01}` for every event.
2. Call `builder.AddKafkaSchemaRegistry(config.GetSection("Fleans:Streaming:Kafka:SchemaRegistry"))` with `Url` pointing to the local SR.

**Steps:**
1. Run a BPMN workflow that passes through a Script Task.
2. Inspect Kafka topics — both `fleans-0` … `fleans-N` AND `fleans-0-events` … `fleans-N-events` topics should exist.
3. Consume a message from `fleans-0-events`.

**Pass:** Both topic families exist; the `-events` topic contains a message with the stub payload `0x01`; no EventId 11107/11108 appear in logs.

---

## Test C — Encoder returning null skips the events topic

**Setup:** Same as Test B but stub `EncodeAsync` returns `null`.

**Steps:**
1. Run a BPMN workflow.
2. Inspect the `fleans-0-events` topic.

**Pass:** Topic exists (created at startup) but contains no new messages for the test workflow; no errors in logs.

---

## Test D — Encoder exception produces EventId 11107 without breaking primary delivery

**Setup:** Stub `EncodeAsync` throws `InvalidOperationException("encode-test-error")`.

**Steps:**
1. Run a BPMN workflow.
2. Check logs.

**Pass:** Workflow completes (primary Orleans delivery unaffected); EventId 11107 WARNING appears in logs with the event type name; EventId 11108 does NOT appear; no exception propagates to the silo.

---

## Test E — SR Basic auth wiring

**Setup:** Configure the local SR with HTTP Basic auth and set:
```
Fleans__Streaming__Kafka__SchemaRegistry__Url=http://localhost:8081
Fleans__Streaming__Kafka__SchemaRegistry__BasicAuthUsername=testuser
Fleans__Streaming__Kafka__SchemaRegistry__BasicAuthPassword=testpassword
```

**Steps:**
1. Call `AddKafkaSchemaRegistry` and register a stub encoder.
2. Produce one event.

**Pass:** SR client authenticates successfully; no 401 in SR logs.

---

## Test F — EventId 11107/11108 remain warnings and do not rethrow

**Setup:** Stub producer throws `ProduceException` on the second `ProduceAsync` call (events topic only).

**Steps:**
1. Run a BPMN workflow.

**Pass:** Workflow completes; EventId 11108 WARNING appears; no exception propagates to the caller; primary topic delivery (EventId absent) is unaffected.
