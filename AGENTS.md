# Agent Guide

This repository contains a .NET workflow engine built on Orleans. The main
solution lives under `src/Fleans`.

## Quick start
- SDK: .NET 10 (see `src/Fleans/global.json`, prerelease allowed)
- Solution: `src/Fleans/Fleans.sln`

From the repo root:
```
cd src/Fleans
dotnet restore
dotnet build --no-restore
dotnet test --no-build --verbosity normal
```

## Running the app
Use the `Fleans.Aspire` project as the startup project. It runs:
- `Fleans.Api` (Orleans silo + workflow engine API)
- `Fleans.Web` (Blazor admin UI)

## Project layout (src/Fleans)
- `Fleans.Api`: API + Orleans hosting
- `Fleans.Application`: workflow engine orchestration
- `Fleans.Domain`: core workflow domain + BPMN model
- `Fleans.Infrastructure`: BPMN conversion + condition evaluation
- `Fleans.ServiceDefaults`: shared DTOs/extensions
- `Fleans.Aspire`: Aspire host for local orchestration
- `*.Tests`: xUnit test projects per layer

## Notes for changes
- Keep updates scoped to the layer you are touching (Domain vs Application vs
  Infrastructure).
- Update or add tests in the matching `*.Tests` project when behavior changes.
