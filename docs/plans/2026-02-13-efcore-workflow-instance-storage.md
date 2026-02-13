# EF Core Grain Storage for WorkflowInstanceState

## Overview

Add an EF Core `IGrainStorage` implementation for `WorkflowInstanceState`, following the same pattern as `EfCoreActivityInstanceGrainStorage`. The state is fully normalized across 4 tables with child collection diffing on updates.

## Database Schema

### WorkflowInstances (main table)

| Column             | Type              | Notes                    |
|--------------------|-------------------|--------------------------|
| `Id`               | `Guid`            | PK (= InstanceId)        |
| `IsStarted`        | `bool`            |                          |
| `IsCompleted`      | `bool`            |                          |
| `CreatedAt`        | `DateTimeOffset?`  |                          |
| `ExecutionStartedAt` | `DateTimeOffset?` |                          |
| `CompletedAt`      | `DateTimeOffset?`  |                          |
| `ETag`             | `string(64)`       | Optimistic concurrency   |

### WorkflowActivityInstanceEntries

| Column               | Type     | Notes                              |
|----------------------|----------|------------------------------------|
| `ActivityInstanceId` | `Guid`   | PK                                 |
| `WorkflowInstanceId` | `Guid`   | FK to WorkflowInstances            |
| `ActivityId`         | `string` |                                    |
| `IsCompleted`        | `bool`   | `false` = active, `true` = completed |

### WorkflowVariableStates

| Column               | Type     | Notes                        |
|----------------------|----------|------------------------------|
| `Id`                 | `Guid`   | PK (dictionary key)          |
| `WorkflowInstanceId` | `Guid`   | FK to WorkflowInstances      |
| `Variables`          | `json`   | Serialized ExpandoObject     |

### WorkflowConditionSequenceStates

| Column                      | Type     | Notes                        |
|-----------------------------|----------|------------------------------|
| `ConditionSequenceStateId`  | `Guid`   | PK (reuses existing domain identity) |
| `WorkflowInstanceId`        | `Guid`   | FK to WorkflowInstances      |
| `GatewayActivityInstanceId` | `Guid`   | Dictionary key (which gateway)|
| `ConditionalSequenceFlowId` | `string` |                              |
| `Result`                    | `bool`   |                              |
| `IsEvaluated`               | `bool`   |                              |

## Domain Class Changes

### WorkflowInstanceState
- Add `Guid Id` and `string? ETag`

### ActivityInstanceEntry
- Add `Guid WorkflowInstanceId` and `bool IsCompleted`
- PK: existing `ActivityInstanceId`

### ConditionSequenceState
- Reuse existing `Guid ConditionSequenceStateId` as PK, add `Guid WorkflowInstanceId`, `Guid GatewayActivityInstanceId`

### WorkflowVariablesState
- Add `Guid WorkflowInstanceId`

## Storage Implementation

### EfCoreWorkflowInstanceGrainStorage

Implements `IGrainStorage`. Registered as keyed singleton with key `"workflowInstances"`.

**ReadStateAsync:**
1. Load main `WorkflowInstances` row
2. Eager-load all child rows from the 3 child tables
3. Split `WorkflowActivityInstanceEntries` by `IsCompleted` into `ActiveActivities` and `CompletedActivities`
4. Reconstruct `VariableStates` dictionary from `WorkflowVariableStates` rows
5. Reconstruct `ConditionSequenceStates` dictionary (group by `GatewayActivityInstanceId`) from `WorkflowConditionSequenceStates` rows

**WriteStateAsync (insert — no existing record):**
1. Assign `Id` from grain ID, generate new `ETag`
2. Insert main row
3. Insert all child rows with appropriate FK and flags (`IsCompleted` for entries)

**WriteStateAsync (update — existing record):**
1. Validate ETag matches; throw `InconsistentStateException` on mismatch
2. Update main row scalars, generate new ETag
3. Diff each child collection against existing DB rows:
   - **ActivityInstanceEntries**: diff by `ActivityInstanceId` — insert new, update changed (`IsCompleted` flag), delete removed
   - **WorkflowVariableStates**: diff by `Id` — insert new, update changed (compare serialized JSON), delete removed
   - **WorkflowConditionSequenceStates**: diff by `(GatewayActivityInstanceId, ConditionalSequenceFlowId)` — insert new, update changed (`Result`, `IsEvaluated`), delete removed

**ClearStateAsync:**
1. Validate ETag
2. Delete main row (cascade deletes handle children)

### Active/Completed List Handling

The storage class merges `ActiveActivities` and `CompletedActivities` into a single table on write (setting `IsCompleted` flag), and splits them back on read. The domain class keeps its two separate lists unchanged.

## Testing

Same pattern as `EfCoreActivityInstanceGrainStorageTests` using in-memory SQLite:

- Round-trip: write + read preserves all scalars, timestamps, child collections
- ETag concurrency: stale ETag throws `InconsistentStateException`
- Child collection diffing: add/update/remove entries across writes
- Variable state: ExpandoObject round-trip through JSON
- Condition state: dictionary reconstruction with grouping
- Clear: cascade deletes all children
- Isolation: different grain IDs don't interfere

## Registration

`DependencyInjection.AddEfCorePersistence()` registers the new storage alongside the existing activity instance storage:
- Key: `"workflowInstances"`
- Type: `EfCoreWorkflowInstanceGrainStorage` as `IGrainStorage`
