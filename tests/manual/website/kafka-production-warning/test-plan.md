# Manual Test Plan — Kafka production-readiness warning (Issue #399)

Verifies that the Kafka streaming page surfaces production-readiness gaps before any v1 reader can copy-paste the env vars and point at a managed broker.

## Prerequisites

- `cd website && npm install` has been run at least once.
- Dev server NOT already running on port 4321.

## Steps

### 1. Build passes

```bash
cd website
npm run build
```

**Expect:** zero errors. Build emits the streaming.md and deployment.md pages without front-matter or Starlight admonition errors.

### 2. Top `:::caution` admonition renders

```bash
grep -c 'Kafka provider is not production-ready' website/dist/reference/streaming/index.html
grep -c 'Disconnected: SASL authentication required' website/dist/reference/streaming/index.html
grep -c 'Confluent Cloud, Amazon MSK, Aiven, Redpanda Cloud' website/dist/reference/streaming/index.html
```

**Expect:** all three counts ≥ 1. The headline `Disconnected: SASL authentication required` should match the form readers see in librdkafka logs (so they can grep their own silo logs for it).

### 3. All three severity tiers render with correct row counts

```bash
grep -c '🔴 Won' website/dist/reference/streaming/index.html
grep -c '🟡 Will lose data' website/dist/reference/streaming/index.html
grep -c '🟢 Operational gaps' website/dist/reference/streaming/index.html
```

**Expect:** all three counts ≥ 1. Visual inspection in the browser must show **4 rows** in 🔴, **3 rows** in 🟡, **4 rows** in 🟢.

### 4. Tracking issue cross-link resolves

```bash
grep -oE 'github.com/nightBaker/fleans/issues/474' website/dist/reference/streaming/index.html | head -1
curl -sI https://github.com/nightBaker/fleans/issues/474 | head -1
```

**Expect:** at least one occurrence in the built HTML, and `HTTP/2 200` from the GitHub URL — issue #474 must exist.

### 5. `deployment.md` cross-link callout renders and resolves

```bash
grep -c 'Streaming: do not point Kafka at managed services in v1' website/dist/reference/deployment/index.html
grep -oE 'reference/streaming/#production-readiness-gaps' website/dist/reference/deployment/index.html
```

**Expect:** count ≥ 1 and the anchor URL is present. Manually verify in a browser that clicking the link navigates to the streaming page anchor (no 404 / no anchor-miss).

### 6. Manual test #35 cross-link still resolves

```bash
ls tests/manual/35-kafka-streaming/test-plan.md
grep -oE 'tests/manual/35-kafka-streaming' website/dist/reference/streaming/index.html | head -1
```

**Expect:** the file exists locally and the path is referenced in the built page.

### 7. Drift-guard line pins still resolve

```bash
sed -n '4,5p' src/Fleans/Fleans.Streaming.Kafka/KafkaStreamingOptions.cs
sed -n '6,23p' src/Fleans/Fleans.Streaming.Kafka/KafkaStreamingOptions.cs | head -3
sed -n '13,15p;57p' src/Fleans/Fleans.Aspire/Program.cs
grep -rE 'SecurityProtocol|SaslMechanism|SaslUsername|SslCa' src/Fleans/Fleans.Streaming.Kafka/ || echo "OK: no SASL/TLS knobs (claim verified)"
```

**Expect:** lines 4-5 of `KafkaStreamingOptions.cs` contain the "Plaintext brokers only — production SASL/TLS lands in a follow-up" XML doc. The grep must report `OK: no SASL/TLS knobs (claim verified)` (zero matches) — the warning's load-bearing claim depends on this.

### 8. Both themes render the admonitions

`npm run dev` and visit `/fleans/reference/streaming/`. Toggle light/dark via the navbar theme switch.

**Expect:** the top `:::caution` block uses Starlight's caution color in both themes (light: amber/orange tint, dark: amber/orange-on-dark). The 🔴/🟡/🟢 emojis render as actual emojis (not text squares). Tables are readable in both themes.

## Verdict

- **PASSED** — all 8 steps green. Move PR to Review by Human.
- **KNOWN BUG** — none expected; this is greenfield docs.
- **FAILED / BUG** — file follow-up issue, send PR back to Ready.
