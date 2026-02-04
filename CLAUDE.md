# CLAUDE.md - AI Assistant Guide for Fleans

## Project Overview

Fleans is a **BPMN workflow engine** built on **Microsoft Orleans** (distributed actor/virtual grain framework). It parses BPMN XML definitions, deploys versioned process definitions, and executes workflow instances as Orleans grains with event-driven state transitions.

## Technology Stack

- **Runtime:** .NET 10.0, C# 14
- **Actor Framework:** Microsoft Orleans 9.2.1
- **Backend API:** ASP.NET Core (controller-based)
- **Frontend:** Blazor Interactive Server with Fluent UI 4.13.2
- **Orchestration:** .NET Aspire 13.1.0
- **State/Clustering:** Redis (via Orleans providers)
- **Expression Evaluation:** DynamicExpresso.Core 2.19.3
- **Testing:** MSTest, NSubstitute 5.3.0, Orleans.TestingHost
- **Observability:** OpenTelemetry 1.14.0

## Repository Structure

```
fleans/
├── .github/workflows/dotnet.yml   # CI pipeline
├── docs/plans/                     # Design documents
├── src/Fleans/
│   ├── Fleans.sln                  # Solution file
│   ├── global.json                 # SDK configuration (.NET 10.0)
│   ├── Fleans.Domain/              # Core domain - grains, activities, events
│   ├── Fleans.Application/         # Application layer - orchestration, handlers
│   ├── Fleans.Infrastructure/      # BPMN parsing, condition evaluation
│   ├── Fleans.Api/                 # REST API + Orleans silo host
│   ├── Fleans.Web/                 # Blazor admin panel + Orleans client
│   ├── Fleans.Aspire/              # .NET Aspire app host
│   ├── Fleans.ServiceDefaults/     # Shared Aspire service configuration
│   ├── Fleans.Domain.Tests/        # Domain unit tests
│   ├── Fleans.Application.Tests/   # Application unit tests
│   └── Fleans.Infrastructure.Tests/# Infrastructure unit tests
└── README.md
```

## Architecture

### Layered Design

1. **Domain** (`Fleans.Domain`) - Core business logic. Orleans grains for workflow instances, activity instances, state management, and domain events. No external dependencies beyond Orleans abstractions.
2. **Application** (`Fleans.Application`) - Orchestration via `WorkflowEngine` facade. Contains factory grains, condition evaluators, event publishers, and stream event handlers.
3. **Infrastructure** (`Fleans.Infrastructure`) - BPMN XML parsing (`BpmnConverter` using `XDocument`), condition evaluation services, and DI registration.
4. **API** (`Fleans.Api`) - REST controllers, Orleans silo host configuration, Redis wiring.
5. **Web** (`Fleans.Web`) - Blazor interactive server components, Orleans client configuration, admin UI for managing workflows.

### Key Domain Concepts

- **WorkflowDefinition** - Stateless BPMN process structure (activities + sequence flows)
- **ProcessDefinition** - Versioned deployment record (key:version:timestamp)
- **WorkflowInstance** (Orleans grain) - Stateful running instance of a process
- **ActivityInstance** (Orleans grain) - Stateful execution of a single activity
- **WorkflowInstanceState** (Orleans grain) - Stores dynamic workflow state
- **Activities** - StartEvent, EndEvent, TaskActivity, ExclusiveGateway, ParallelGateway, ErrorEvent
- **SequenceFlow / ConditionalSequenceFlow** - Connections between activities

### Versioning Model (Camunda-inspired)

- **Process Definition Key** = BPMN `<process id>` attribute
- **Version** = Auto-incrementing integer per key
- **Process Definition ID** = `key:version:timestamp` (unique identifier)
- Deployments are immutable; starting a workflow resolves the latest version for a key

### Orleans Grain Types

| Grain | Key Type | Purpose |
|-------|----------|---------|
| `IWorkflowInstance` | Guid | Executes a workflow |
| `IActivityInstance` | Guid | Executes a single activity |
| `IWorkflowInstanceState` | Guid | Stores workflow state |
| `IWorkflowInstanceFactoryGrain` | Integer | Singleton factory for deployments/starts |
| `IEventPublisher` | Integer | Singleton event publishing hub |
| `IConditionExpressionEvaluaterGrain` | - | Evaluates gateway conditions |

### API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| POST | `/workflow/upload-bpmn` | Upload and deploy a BPMN file |
| POST | `/workflow/register` | Register a workflow definition |
| POST | `/workflow/start` | Start a new workflow instance |
| GET | `/workflow/all` | List all deployed process definitions |

## Build & Development Commands

All commands should be run from the `src/Fleans` directory:

```bash
# Restore dependencies
dotnet restore

# Build the solution
dotnet build

# Run all tests
dotnet test

# Run tests with verbosity
dotnet test --verbosity normal

# Run a specific test project
dotnet test Fleans.Domain.Tests
dotnet test Fleans.Application.Tests
dotnet test Fleans.Infrastructure.Tests

# Run the application (Aspire host)
dotnet run --project Fleans.Aspire
```

## CI/CD Pipeline

Defined in `.github/workflows/dotnet.yml`:
- **Triggers:** Push to `main`, PRs against `main`
- **Runner:** `ubuntu-latest`
- **Steps:** Checkout -> Setup .NET 10.0.x -> Restore -> Build -> Test
- **Working directory:** `src/Fleans`

## Testing Conventions

- **Framework:** MSTest (`[TestClass]`, `[TestMethod]`)
- **Mocking:** NSubstitute (`Substitute.For<T>()`)
- **Orleans Testing:** `Orleans.TestingHost` for grain integration tests
- **Coverage:** coverlet.collector
- **Test project naming:** `Fleans.<Layer>.Tests` mirrors `Fleans.<Layer>`
- Tests are organized by domain concept (e.g., `WorkflowInstanceTests`, `ExclusiveGatewayTests`, `TaskActivityTests`)

## Code Conventions

- **Nullable reference types** enabled across all projects
- **Implicit usings** enabled
- **C# 14** language features available
- **Records** used for data transfer objects (`ProcessDefinition`, `ProcessDefinitionSummary`)
- **Orleans `[Reentrant]`** attribute used on grains that handle concurrent calls (e.g., `WorkflowInstanceFactoryGrain`)
- **Grain interfaces** prefixed with `I` and placed alongside their implementations or in a shared location
- **Domain events** implement `IDomainEvent` interface
- **Event handlers** follow the `IWorkflowEventsHandler` pattern with method-per-event-type

## Key Files for Common Tasks

| Task | Key Files |
|------|-----------|
| Add a new BPMN activity type | `src/Fleans/Fleans.Domain/Activities/`, `Activity.cs` (base class) |
| Modify workflow execution | `src/Fleans/Fleans.Domain/WorkflowInstance.cs` |
| Change BPMN parsing | `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs` |
| Add/modify API endpoints | `src/Fleans/Fleans.Api/Controllers/WorkflowController.cs` |
| Modify the admin UI | `src/Fleans/Fleans.Web/Components/Pages/` |
| Change Aspire configuration | `src/Fleans/Fleans.Aspire/Program.cs` |
| Add event handling | `src/Fleans/Fleans.Application/Events/Handlers/` |
| Modify condition evaluation | `src/Fleans/Fleans.Application/Conditions/` |
| Change deployment/versioning | `src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs` |

## Design Documents

Design plans are in `docs/plans/` and describe architectural decisions:
- `2026-01-14-bpmn-upload-design.md` - BPMN upload and Camunda-style versioning
- `2026-01-25-workflow-deployments-visibility-design.md` - Split-view workflow management UI

## Important Notes

- The `global.json` specifies .NET SDK 10.0 with `rollForward: latestMajor` and `allowPrerelease: true`
- Redis is required for Orleans clustering, grain directory, and persistence (configured via Aspire in dev)
- The API project (`Fleans.Api`) hosts the Orleans silo; the Web project (`Fleans.Web`) connects as an Orleans client
- BPMN elements have partial implementation - see the README.md table for current status
- **Admin UI development:** Large pages should be split into smaller reusable components (see `Workflows.razor` with `WorkflowKeysPanel.razor`, `WorkflowVersionsPanel.razor`, `WorkflowUploadPanel.razor` as an example)
