namespace Fleans.ServiceDefaults.DTOs;

/// <param name="PurgedCount">Instances deleted by this call.</param>
/// <param name="HasMore">More finished instances match the cutoff — call again.</param>
public record PurgeInstancesResponse(int PurgedCount, bool HasMore);
