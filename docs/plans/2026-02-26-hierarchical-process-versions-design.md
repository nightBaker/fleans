# Hierarchical Process Versions DataGrid

**Date:** 2026-02-26

## Goal

Replace the flat process-definitions list on the Workflows page with a hierarchical DataGrid that groups versions under their process key. Uses the `IHierarchicalGridItem` feature introduced in Fluent UI Blazor 4.14.

## Package Update

Bump Fluent UI Blazor packages in `Fleans.Web.csproj` from 4.13.2 → 4.14.0:

- `Microsoft.FluentUI.AspNetCore.Components`
- `Microsoft.FluentUI.AspNetCore.Components.Emoji`
- `Microsoft.FluentUI.AspNetCore.Components.Icons`

## Data Model

`ProcessDefinitionRow` — a wrapper around `ProcessDefinitionSummary` that implements `IHierarchicalGridItem`:

| Property | Parent row (Depth=0) | Child row (Depth=1) |
|----------|---------------------|---------------------|
| `Depth` | 0 | 1 |
| `HasChildren` | true if >1 version | false |
| `IsCollapsed` | starts true | N/A |
| `IsHidden` | false | true (hidden until parent expanded) |

Parent row = latest version of each `ProcessDefinitionKey`.
Child rows = older versions, ordered descending.

## Data Transformation

In `LoadWorkflows()`:

1. Fetch all definitions via `QueryService.GetAllProcessDefinitions()`
2. Group by `ProcessDefinitionKey`
3. Within each group, sort versions descending
4. First item → parent (Depth=0), remainder → children (Depth=1)
5. Flatten into `List<ProcessDefinitionRow>` preserving parent→children order

## Grid Layout

First column (`Process Key`) sets `HierarchicalToggle="true"` for the expand/collapse chevron.

| Column | Content |
|--------|---------|
| Process Key | Bold key name (parent shows chevron) |
| Version | `FluentBadge` with version number |
| Deployed at (UTC) | Timestamp |
| Actions | Edit, Start, Instances buttons |

Both parent and child rows show identical column content — the parent just happens to show the latest version's data.

## Search/Filter

Filter by `ProcessDefinitionKey` — include parent + all children when key matches. No UX change.

## Scope

- UI-only change in `Workflows.razor`
- No backend / query service changes
- No new endpoints
