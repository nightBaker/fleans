---
title: Architecture
description: How Fleans is built.
---

Fleans follows a Clean Architecture / DDD layering:

- **Fleans.Domain** — aggregates, value objects, and domain events. Pure C#, no infrastructure.
- **Fleans.Application** — Orleans grains, command/query services, orchestration.
- **Fleans.Infrastructure** — BPMN parsing (Camunda XML), EF Core persistence providers.
- **Fleans.Api** — ASP.NET Core REST API.
- **Fleans.Web** — Blazor Server admin UI.
- **Fleans.Aspire** — .NET Aspire host that wires everything together for local dev.

The central grain is `WorkflowInstance`, an Orleans `JournaledGrain` that uses event sourcing for
state, with a read-side projection written to EF Core.
