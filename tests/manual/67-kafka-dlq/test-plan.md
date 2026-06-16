# Manual Test Plan — Kafka DLQ (Dead-Letter Queue) for Poison Messages

**Feature:** #686 `[feat:streaming-kafka] DLQ for poison messages (max-retry + dead-letter topic)`  
**Status:** NEEDS-RERUN

## Prerequisites

- Docker running (for Kafka via Aspire)
- `FLEANS_STREAMING_PROVIDER=Kafka` set in your environment (or `Fleans__Streaming__Provider=Kafka`)
- `FLEANS_STREAMING_KAFKA_ENABLE_DEAD_LETTER_QUEUE=true` (or `Fleans__Streaming__Kafka__EnableDeadLetterQueue=true`)
- `FLEANS_STREAMING_KAFKA_MAX_CONSUMER_RETRIES=2` (so tests complete faster; default 3)

## BPMN fixture: `poison-message.bpmn`

A workflow that receives a message event and its handler script intentionally throws on the
first N deliveries to simulate a poison message. After max retries the DLQ handler should
route the raw Kafka bytes to the `-dlq` topic.

## Steps

### Step 1 — Start the stack

```bash
dotnet run --project src/Fleans/Fleans.Aspire
```

Verify: Aspire dashboard shows `fleans-kafka` resource RUNNING and the silo is `Running`.

**Expected:** Silo log contains `Kafka topic-ensure: created` for both `fleans-stream-0` through
`fleans-stream-7` **and** `fleans-stream-0-dlq` through `fleans-stream-7-dlq` (DLQ topics
auto-created because `EnableDeadLetterQueue=true`).

### Step 2 — Deploy the fixture workflow

Use the Fleans admin UI or `POST /Workflow/deploy` with `poison-message.bpmn`.

**Expected:** Deployment succeeds; workflow appears in the admin UI process list.

### Step 3 — Start a workflow instance

`POST https://localhost:7140/Workflow/start` — body: `{"WorkflowId":"poison-message-process"}`

**Expected:** Instance started; silo log shows `Kafka consumer received message`.

### Step 4 — Send a message that the handler will reject twice

`POST https://localhost:7140/Workflow/message` — body:
```json
{"MessageName": "PoisonMsg", "CorrelationKey": "<instanceId>", "Variables": {}}
```

**Expected (with `MaxConsumerRetries=2`):**
- Retry 1: silo log EventId 12006 `DLQ handler: retry 1/2 for stream …`
- Retry 2: silo log EventId 12006 `DLQ handler: retry 2/2 for stream …`
- After retry 2: silo log EventId 12012 `DLQ handler: published stream … to fleans-stream-N-dlq`

### Step 5 — Verify DLQ topic received the message

```bash
docker exec -it $(docker ps -q --filter name=kafka) \
  kafka-console-consumer.sh --bootstrap-server localhost:9092 \
  --topic fleans-stream-0-dlq --from-beginning --max-messages 1 --timeout-ms 5000
```

**Expected:** Raw bytes of the original Kafka message appear (not empty). Headers visible via:
```bash
kafka-console-consumer.sh … --property print.headers=true
```
Must include: `x-fleans-original-topic`, `x-fleans-original-offset`, `x-fleans-retry-count`,
`x-fleans-failure-time-utc`, `x-fleans-stream-id`.

### Step 6 — Verify source offset was committed (restart safety)

Restart the silo (Ctrl-C → `dotnet run --project src/Fleans/Fleans.Aspire`).

**Expected:** Silo does NOT re-deliver the already-DLQ'd message. No second EventId 12012 log.
Offset is committed (the consumer group's offset for that partition advanced past the poison
message's offset + 1).

### Step 7 — Verify DLQ disabled path (regression guard)

Remove `EnableDeadLetterQueue` config (default `false`). Repeat steps 2–4, sending the `PoisonMsg`
message to trigger repeated delivery failures (the message event handler will fail on each delivery
because no workflow instance can process it — the `poison-message-process` instance was already
completed or the correlation fails, causing Orleans to call `OnSubscriptionFailure` repeatedly).

**Expected:** No `-dlq` topics auto-created. The stream subscription retries indefinitely (no DLQ
routing); no EventIds 12006–12015 appear in silo logs.

## Pass criteria

- Steps 1–6 complete with no unexpected errors.
- DLQ topic receives exactly one copy of the poison message (at-least-once; a duplicate is
  acceptable but must not happen in the happy path).
- Source offset advances past the poison message's offset so silo restart doesn't re-deliver.
- EventId sequence in silo logs: 12006 × (MaxConsumerRetries-1) → 12012 → 12014.
