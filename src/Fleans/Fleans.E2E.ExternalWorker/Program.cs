using Fleans.E2E.ExternalWorker;
using Fleans.ExternalTasks.Sdk;
using Microsoft.Extensions.Hosting;

// Test-only external-task worker (E2E suite, tests/load/overrides/grpc-worker.yml). Its whole
// configuration is Fleans:ExternalTaskWorker:EngineUrl; Probe:TaskType picks the task type it
// serves (default e2e-ext-probe; the load override reuses the fixtures' e2e-probe).
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddFleansExternalTaskWorker();
builder.Services.AddExternalTaskHandler<ProbeHandler>(
    builder.Configuration["Probe:TaskType"] ?? ProbeHandler.DefaultTaskType,
    displayName: "E2E External Probe");

builder.Build().Run();
