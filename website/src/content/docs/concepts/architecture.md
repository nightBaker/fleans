---
title: Architecture
description: How Fleans achieves scalability, reliability, and performance through Orleans virtual actors and event sourcing.
---

## Layer overview

Fleans follows Clean Architecture / DDD layering:

| Layer | Project | Responsibility |
|-------|---------|---------------|
| Domain | `Fleans.Domain` | Aggregates, value objects, domain events. Pure C#, no infrastructure. |
| Application | `Fleans.Application` | Orleans grains, command/query services, orchestration. |
| Infrastructure | `Fleans.Infrastructure` | BPMN parsing (Camunda XML), expression evaluation (DynamicExpresso). |
| Persistence | `Fleans.Persistence.*` | EF Core storage — SQLite (dev) and PostgreSQL (production). |
| API | `Fleans.Api` | ASP.NET Core REST API with rate limiting. |
| Admin UI | `Fleans.Web` | Blazor Server admin UI — calls grains directly, no HTTP hop. |
| Host | `Fleans.Aspire` | .NET Aspire host that wires everything together for local dev. |

The central grain is **`WorkflowInstance`** — an Orleans `JournaledGrain` that uses event sourcing for workflow state, with a read-side projection written to EF Core (CQRS).

## What Orleans gives us

[Orleans](https://learn.microsoft.com/dotnet/orleans/) is Microsoft's virtual actor framework. Every workflow instance in Fleans is an **Orleans grain** — a lightweight, addressable object that lives in memory on one silo (server) at a time. You never manage threads, locks, or instance routing yourself. Orleans handles activation, placement, and failover transparently.

This means Fleans inherits Orleans' distributed systems guarantees without building them from scratch: automatic cluster membership, location-transparent grain references, single-threaded grain execution (no concurrency bugs inside a workflow), and grain-level failure isolation.

## Scalability

**Linear horizontal scaling.** Each `WorkflowInstance` grain is an independent unit with no shared coordinator. Adding silos to the Orleans cluster increases capacity linearly — grains redistribute automatically via cluster membership protocol.

**Location transparency.** A grain reference is stable regardless of which silo hosts the grain. Orleans routes calls to the correct silo. Your code never needs to know where a workflow instance lives.

**Lazy activation.** Grains activate on first access and deactivate after idle timeout. Cluster memory scales with *active* workflows, not total historical instances. A cluster with 10 million completed workflows and 100 active ones uses memory for ~100 grains.

**CQRS database split.** Writes go through `FleanCommandDbContext`; reads go through `FleanQueryDbContext` (with `NoTracking` by default). PostgreSQL deployments can route reads to a replica via `ConnectionStrings:fleans-query` — no code changes, just configuration.

**Fan-out via child grains.** Embedded sub-processes and call activities spawn their own `WorkflowInstance` grains. A parent workflow fans work across the cluster automatically.

**Direct grain access from admin UI.** The Blazor Server admin panel (`Fleans.Web`) calls Orleans grains directly via the `WorkflowEngine` service — no HTTP API hop between the operator and workflow state.

## Reliability

**Event-sourced workflow state.** `WorkflowInstance` extends `JournaledGrain` with an append-only event log. State is reconstructed deterministically from events. After a silo crash, Orleans reactivates the grain on a surviving silo and replays the log — no workflow state is lost.

**Atomic state transitions.** Events drain via `DrainAndRaiseEvents()` + `ConfirmEvents()` in a single transaction. A crash mid-execution never leaves a workflow half-advanced — either the full batch of events persists, or none do.

**Per-workflow failure isolation.** Each grain has its own event stream and lifecycle. A bug or exception in one workflow instance cannot corrupt another.

**Automatic silo failover.** When a silo leaves the cluster (crash, scale-down, rolling deploy), Orleans detects the membership change and reactivates affected grains on surviving silos. No manual intervention required.

**Typed recovery paths.** BPMN error boundary events, escalation events, and compensation handlers provide structured, spec-compliant recovery — not ad-hoc try/catch.

**Structured audit trail.** Every state-mutating grain method has a `[LoggerMessage]` log call with documented EventId ranges. No silent state mutations.

**Automatic schema management.** PostgreSQL uses `MigrateAsync()` on startup; SQLite uses `EnsureCreated()`. No manual migration steps on deploy.

## Performance

**In-memory grain state.** Mid-workflow steps read and write in-memory state on the hosting silo. Zero database round-trips until the next `ConfirmEvents()` flush.

**Materialized read-side projection.** The `EfCoreWorkflowStateProjection` writes a denormalized snapshot after each event batch. UI queries and `GET /instances/{id}/state` never replay events — they read the projection directly.

**NoTracking reads by default.** The query `DbContext` skips EF Core change-tracking overhead on every read.

**Event batching.** All events from a single grain call persist in one transaction via `ConfirmEvents()`. A workflow step that emits 5 domain events makes 1 database write, not 5.

**Binary intra-cluster protocol.** Orleans silos communicate via a binary protocol, not HTTP. Grain-to-grain calls within the cluster have minimal serialization overhead.

**Dynamic variables without schema migration.** Workflow variables use `ExpandoObject` + Newtonsoft.Json. Adding new variables to a BPMN process never requires a database migration.

## Deployment matrix

| Environment | Persistence | Clustering | Use case |
|-------------|------------|------------|----------|
| **Local dev** | SQLite (`Persistence:Provider=Sqlite`) | Localhost (single silo via Aspire) | Development, debugging, unit tests |
| **Production** | PostgreSQL (`Persistence:Provider=Postgres`) | Redis (`FLEANS_STANDALONE=true`) | Multi-silo cluster, horizontal scaling |
| **Load testing** | PostgreSQL (write-tuned) | Redis (Docker Compose) | Performance benchmarking with k6 |

Switching providers is configuration-only — set `Persistence:Provider` and the appropriate connection string. No code changes needed. See the [Persistence reference](/fleans/reference/persistence/) for details.

## Core and Worker silos

Fleans ships as a single binary (`Fleans.Api`) that can run in three roles, selected via the `Fleans:Role` configuration key:

| Role | What the silo hosts |
|------|--------------------|
| `Combined` *(default)* | Everything — WorkflowInstance, ProcessDefinition, timers, correlations, user tasks, **and** the StatelessWorker script/condition evaluators. Right choice for single-node deployments. |
| `Core` | Workflow coordination grains and event-sourcing storage. Delegates script/condition evaluation to a Worker silo. |
| `Worker` | The StatelessWorker evaluators (`ScriptExecutorGrain`, `ConditionExpressionEvaluatorGrain`) and nothing else. Isolates CPU-bound script execution from workflow state. |

The role is stamped onto the silo name as `{role}-{machine}-{guid}`, so the Orleans Dashboard (and other silos via membership gossip) can see which role each silo is running. Set the role via the `Fleans__Role` environment variable, an `appsettings.json` entry, or a command-line argument.

A small deployment stays on `Combined`. Splitting to `Core` + scaled `Worker` instances is an operational choice you can make later without code changes.
