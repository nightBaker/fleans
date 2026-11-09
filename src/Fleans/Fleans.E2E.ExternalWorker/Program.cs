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
    parameterSchema: new ExternalTaskParameterSchema(
    [
        new ExternalTaskParameter("delayMs", "Delay (ms)", ExternalTaskParameterType.Integer),
        new ExternalTaskParameter("failCode", "Fail with code", ExternalTaskParameterType.String),
        new ExternalTaskParameter("callbackUrl", "Callback URL", ExternalTaskParameterType.String),
        new ExternalTaskParameter("probeKey", "Probe key", ExternalTaskParameterType.String),
    ]));
builder.Services.AddExternalTaskHandler<ProbeHandler>(
    ProbeHandler.ExternalProbeTaskType,
    displayName: "E2E External Probe");
// For the catalog's category and allow-list (#1022).
builder.Services.AddExternalTaskHandler<ProbeHandler>(
    ProbeHandler.PrivateTaskType,
    displayName: "E2E Private Probe",
    category: "E2E",
    visibility: ExternalTaskVisibility.Private);
// Declares that it takes nothing, unlike a handler with no schema, which has no contract.
builder.Services.AddExternalTaskHandler<ProbeHandler>(
    ProbeHandler.NoParametersTaskType,
    displayName: "E2E No Parameters",
    parameterSchema: ExternalTaskParameterSchema.Empty);
// For plugin versions and the input contract (#1051).
builder.Services.AddExternalTaskHandler<ProbeHandler>(
    ProbeHandler.VersionedTaskType,
    displayName: "E2E Versioned Probe",
    version: ProbeHandler.VersionedTaskVersion,
    parameterSchema: new ExternalTaskParameterSchema(
    [
        new ExternalTaskParameter("delayMs", "Delay (ms)", ExternalTaskParameterType.Integer, Required: true),
        new ExternalTaskParameter("codes", "Codes", ExternalTaskParameterType.List, ItemType: ExternalTaskParameterType.Integer),
    ]));

builder.Build().Run();
