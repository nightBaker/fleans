namespace Fleans.ServiceDefaults.DTOs;

/// <summary>
/// <c>POST /Instances/purge</c> (#862). <see cref="CompletedBefore"/> is required; the sizes fall
/// back to the service defaults (500 per batch, 10 000 per call).
/// </summary>
public record PurgeInstancesRequest(DateTimeOffset? CompletedBefore, int? BatchSize = null, int? MaxInstances = null);
