---
title: Glossary
description: Short definitions of the Orleans, event-sourcing and task terms used across the Fleans documentation.
---

The terms below are used throughout this site. Each links to the page that explains it in depth.

## Cluster and hosts

**Orleans** — Microsoft's virtual-actor framework for .NET, which Fleans is built on. See [Architecture](/fleans/concepts/architecture/#what-orleans-gives-us).

**Grain** — an Orleans virtual actor: an addressable object with its own state that lives in memory on one silo at a time and handles one call at a time. Every workflow instance is a grain.

**Activation** — a grain loaded into memory on a silo. A grain is activated on its first call and deactivated after it is idle; its state survives in storage in between.

**Silo** — a server process that hosts grains. The silos together form the **cluster**; they find each other through the membership table (Redis or PostgreSQL).

**Orleans client** — a process that calls grains but hosts none. `fleans-web`, `fleans-mcp` and `fleans-api` in the `Api` role are clients.

**Role** — what a Fleans silo hosts, set with `Fleans:Role` ([Configuration](/fleans/reference/configuration/#silo-role)):

| Role | Hosts |
|---|---|
| `Combined` (default) | everything — the right choice for one node |
| `Core` | workflow instances and the grains that coordinate them (timers, correlation, user tasks) |
| `Worker` | script execution, conditional-start evaluation and the read-model projector |
| `Api` | nothing — runs the REST API and the external-worker gRPC endpoint as an Orleans client |

**Host / workload** — one deployable process. The images are `fleans-api` (the engine host, any role), `fleans-worker` (Worker role only), `fleans-web` (admin UI) and `fleans-mcp`. Deployments name them after their role: in the Compose bundle the services are `fleans-core`, `fleans-worker`, `fleans-management` (the admin UI) and `fleans-mcp`.

**Reminder** — a durable Orleans timer kept in the reminder store (Redis or PostgreSQL). BPMN timers are reminders.

**Stream provider** — the queue Orleans streams run on (Redis by default, Kafka, Azure Queue, or in-memory for one silo). Custom-task hand-offs, read-model notifications and signal deliveries travel on streams. See [Streaming](/fleans/reference/streaming/).

## State and storage

**Journal (event log)** — the append-only list of events that records everything a workflow instance did. It is the source of truth; an instance's state is rebuilt by replaying it. See [Persistence](/fleans/reference/persistence/).

**Event sourcing** — storing state as that journal of events instead of overwriting a row.

**Snapshot** — a copy of an instance's state written every 500 events, so activation replays only the events after it. It is derived from the journal.

**Read model / projection** — tables built from the journal for lists, filters and finished instances, so they never replay events. A **projector** on the worker silos writes them shortly after each change, so lists can lag the live state by a moment.

**CQRS** — command/query responsibility segregation: writes (commands) go to the instance and its journal, reads (queries) go to the read model, which can sit on a replica.

## Processes and instances

**Process definition** — a deployed BPMN process. Each deploy of the same process id creates a new **version**.

**Workflow instance / process instance** — one run of a process definition, identified by a random Guid (`WorkflowInstanceId`) and optionally a **business id** you choose, such as an order number.

**Activity** — a BPMN element that does or waits for something: a task, event, gateway or sub-process.

**Activity instance** — one execution of an activity within an instance. A loop or multi-instance activity creates a new activity instance per pass; each one completes or fails exactly once.

**Instance migration** — moving running instances to another version of their process. See [Instance migration](/fleans/guides/instance-migration/).

## Tasks and workers

**Service task** — a `<bpmn:serviceTask>`. Without a `type`, it waits until your code completes it through the REST API ([Service tasks](/fleans/guides/service-tasks/)).

**Custom task** — a service task with a `type`, executed by a worker that implements that type ([Custom tasks](/fleans/concepts/custom-tasks/)).

**External task** — the engine's word for a custom task handed to a worker: queued, delivered over gRPC, and completed or failed by the worker.

**External worker** — a process of yours that connects to the Fleans API over gRPC, receives the tasks of the types it implements and reports each outcome. It does not join the cluster. See [External workers](/fleans/guides/external-workers/).

**Handler** — the class in a worker that runs one task type (in .NET, an `ExternalTaskHandler`).

**Plugin** — a packaged, ready-made worker with its handlers, such as those in [fleans-plugins](https://github.com/nightBaker/fleans-plugins) (REST Caller, Camunda external tasks).

**Script task** — a task whose C#-like script runs inside the cluster — on a Worker or Combined silo when there is one, otherwise on any silo — not on an external worker.

**User task** — a task a person claims and completes, through the admin UI or the API ([User tasks](/fleans/guides/user-tasks/)).
