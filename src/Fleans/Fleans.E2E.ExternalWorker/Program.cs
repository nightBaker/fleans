using Fleans.E2E.ExternalWorker;
using Fleans.ExternalTasks.Sdk;
using Microsoft.Extensions.Hosting;

// Test-only custom-task worker (E2E suite, tests/load). Its whole configuration is
// Fleans:ExternalTaskWorker:EngineUrl.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddFleansExternalTaskWorker();
builder.Services.AddExternalTaskHandler<ProbeHandler>(
    ProbeHandler.ProbeTaskType,
    displayName: "E2E Probe",
    parameterSchema: ExternalTaskParameterSchema.Empty);
builder.Services.AddExternalTaskHandler<ProbeHandler>(
    ProbeHandler.ExternalProbeTaskType,
    displayName: "E2E External Probe");
// For the catalog's category and allow-list (#1022).
builder.Services.AddExternalTaskHandler<ProbeHandler>(
    ProbeHandler.PrivateTaskType,
    displayName: "E2E Private Probe",
    category: "E2E",
    visibility: ExternalTaskVisibility.Private);

builder.Build().Run();
