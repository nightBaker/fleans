# Manual Test Plan #56 — Streaming Queue Count Configuration

Verifies the Orleans-parallelism knob plumbing added by #567:
- Redis `Fleans:Streaming:Redis:TotalQueueCount` is read from config (default `8`); invalid values throw `ArgumentException` at startup with a clear message.
- Kafka `Fleans:Streaming:Kafka:QueueCount` default raised from `1` → `8`.

**Scope.** This plan is intentionally **single-silo Aspire dev mode**. `HashRingBasedStreamQueueMapper` distributes pulling agents across the cluster via the hash ring — in a multi-silo deployment, each silo hosts `≈ ceil(TotalQueueCount / silo_count)` agents, not `TotalQueueCount` per silo. Use the Orleans dashboard cluster-wide for production sizing; this plan only pins the single-silo behaviour.

## Prerequisites

- Aspire stack stopped (no `dotnet run --project Fleans.Aspire` running yet).
- Clean dev DB (delete the SQLite file or set a fresh `FLEANS_SQLITE_CONNECTION`).
- The Orleans dashboard is wired in `Fleans.Api/Program.cs` via `siloBuilder.AddDashboard()` — its UI is served by `Fleans.Web` (cross-link: see the prior in-repo plan for how to reach it).

## Step 1 — Default (knob absent): 8 pulling agents

1. Confirm `Fleans__Streaming__Redis__TotalQueueCount` is NOT set in any env file or shell.
2. `dotnet run --project Fleans.Aspire` from `src/Fleans/`. Wait for the `fleans-core` resource to report `Running` in the Aspire dashboard.
3. Open the Orleans dashboard (linked from the Aspire dashboard's `fleans-core` row → `/dashboard`).
4. Navigate to the **System Targets** view (or grain-activations view, depending on the Orleans dashboard version).
5. Filter / search for `PersistentStreamPullingAgent`.

**Expected:** exactly **8** `PersistentStreamPullingAgent` activations are present (one per Orleans queue in the single-silo cluster).

Record result.

## Step 2 — Configured value (16): 16 pulling agents

1. Stop the Aspire stack (`Ctrl-C`).
2. Set the env var: `export Fleans__Streaming__Redis__TotalQueueCount=16`.
3. `dotnet run --project Fleans.Aspire` again.
4. Wait for `fleans-core` to be `Running`. Re-open the Orleans dashboard.
5. Filter for `PersistentStreamPullingAgent`.

**Expected:** exactly **16** `PersistentStreamPullingAgent` activations are present. No `ArgumentException` in the silo console log.

Record result.

## Step 3 — Invalid value (`0`): silo aborts at startup

1. Stop the Aspire stack.
2. Set the env var: `export Fleans__Streaming__Redis__TotalQueueCount=0`.
3. `dotnet run --project Fleans.Aspire`.

**Expected:** the `fleans-core` resource fails to start. The Aspire dashboard's `fleans-core` console log contains:

```
System.ArgumentException: Fleans:Streaming:Redis:TotalQueueCount must be >= 1 (got 0). Set Fleans__Streaming__Redis__TotalQueueCount to a positive integer.
```

Record result.

## Step 4 — Invalid value (non-integer): silo aborts at startup with a typo-friendly message

1. Stop the Aspire stack.
2. Set the env var: `export Fleans__Streaming__Redis__TotalQueueCount=eight`.
3. `dotnet run --project Fleans.Aspire`.

**Expected:** the `fleans-core` resource fails to start. The Aspire dashboard's `fleans-core` console log contains:

```
System.ArgumentException: Fleans:Streaming:Redis:TotalQueueCount must be an integer (got 'eight'). Set Fleans__Streaming__Redis__TotalQueueCount to a positive integer.
```

Record result.

## Step 5 — Kafka default reads `QueueCount=8`

1. Stop the Aspire stack. `unset Fleans__Streaming__Redis__TotalQueueCount`.
2. `export FLEANS_STREAMING_PROVIDER=Kafka`. Do NOT set any `Fleans__Streaming__Kafka__*` overrides.
3. `dotnet run --project Fleans.Aspire`. Wait for the `fleans-kafka` and `fleans-core` resources to report `Running`.
4. From a terminal, list Kafka topics: `docker exec -it $(docker ps -qf name=fleans-kafka) kafka-topics --bootstrap-server localhost:9092 --list` (or use whatever `kafka-topics` flag is appropriate for the bundled image).

**Expected:** **8** topics with the `fleans-` prefix exist (`fleans-0`, `fleans-1`, …, `fleans-7`). Each topic has **1** partition (verify with `kafka-topics --describe --topic fleans-0 --bootstrap-server localhost:9092` and check `PartitionCount: 1`).

Record result.

## Cleanup

```bash
unset Fleans__Streaming__Redis__TotalQueueCount
unset FLEANS_STREAMING_PROVIDER
```

Stop the Aspire stack and delete the dev DB if you ran this immediately before another test plan.

## Out of scope (covered elsewhere)

- Multi-silo agent distribution behaviour — exercised by the load-testing infrastructure, not this plan.
- AzureQueue tuning above 8 entries — covered by `tests/manual/44-azure-queue-streaming/` extended once the operator path is documented.
- End-to-end throughput measurements — separate baseline issue (#593, scheduled after #567 lands).
