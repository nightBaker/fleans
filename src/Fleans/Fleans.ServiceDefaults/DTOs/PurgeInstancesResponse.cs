namespace Fleans.ServiceDefaults.DTOs;

/// <param name="PurgedCount">Instances deleted by this call.</param>
/// <param name="HasMore">More finished instances or expired idempotency records remain — call again.</param>
/// <param name="PurgedDeduplicationCount">Expired message idempotency records deleted by this call (#977).</param>
public record PurgeInstancesResponse(int PurgedCount, bool HasMore, int PurgedDeduplicationCount = 0);
