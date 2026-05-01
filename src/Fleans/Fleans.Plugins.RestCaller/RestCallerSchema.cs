using Fleans.Application.CustomTasks;

namespace Fleans.Plugins.RestCaller;

/// <summary>
/// The parameter schema the management UI's BPMN editor will render for
/// <c>&lt;serviceTask type="rest-call"&gt;</c>. Static so tests and the DI extension
/// can both reference the same instance.
/// </summary>
public static class RestCallerSchema
{
    public static readonly CustomTaskParameterSchema Default = new(new[]
    {
        new CustomTaskParameterSpec(
            Name: "url",
            DisplayName: "URL",
            Type: CustomTaskParameterType.String,
            Required: true,
            Description: "Absolute URI. Use =workflowVar to source dynamically.",
            DefaultValue: null),

        new CustomTaskParameterSpec(
            Name: "method",
            DisplayName: "HTTP Method",
            Type: CustomTaskParameterType.String,
            Required: true,
            Description: "GET, POST, PUT, PATCH, DELETE, HEAD, or OPTIONS.",
            DefaultValue: "GET"),

        new CustomTaskParameterSpec(
            Name: "headers",
            DisplayName: "Request Headers",
            Type: CustomTaskParameterType.Map,
            Required: false,
            Description: "Each (header-name, header-value) pair. In v1, must be sourced from a workflow variable holding a map.",
            DefaultValue: null,
            ItemType: CustomTaskParameterType.String),

        new CustomTaskParameterSpec(
            Name: "body",
            DisplayName: "Request Body",
            Type: CustomTaskParameterType.MultilineString,
            Required: false,
            Description: "Sent verbatim. If non-empty and no Content-Type header is supplied, defaults to application/json.",
            DefaultValue: null),

        new CustomTaskParameterSpec(
            Name: "successCodes",
            DisplayName: "Success Codes",
            Type: CustomTaskParameterType.List,
            Required: false,
            Description: "HTTP status codes treated as success. Empty/null defaults to 200..299. In v1, must be sourced from a workflow variable holding a list.",
            DefaultValue: null,
            ItemType: CustomTaskParameterType.Integer),

        new CustomTaskParameterSpec(
            Name: "timeoutSec",
            DisplayName: "Timeout (seconds)",
            Type: CustomTaskParameterType.Integer,
            Required: true,
            Description: "Whole seconds; clamped to [1, 300]. Timeout fails the activity with code=504.",
            DefaultValue: "30"),

        new CustomTaskParameterSpec(
            Name: "idempotencyKeyHeader",
            DisplayName: "Idempotency Key Header",
            Type: CustomTaskParameterType.String,
            Required: false,
            Description: "When set, plugin sends <header>: <activityInstanceId-guid> so server-side dedupe is keyed on the activity instance id (mitigates retries under silo failure).",
            DefaultValue: null),
    });
}
