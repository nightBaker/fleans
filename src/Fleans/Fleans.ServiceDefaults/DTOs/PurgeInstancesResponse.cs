namespace Fleans.ServiceDefaults.DTOs;

/// <param name="PurgedCount">Instances deleted by this call.</param>
/// <param name="HasMore">More finished instances or expired message-deduplication records remain — call again.</param>
/// <param name="PurgedMessageDeduplicationsCount">Expired message-deduplication records deleted by this call.</param>
public record PurgeInstancesResponse(int PurgedCount, bool HasMore, int PurgedMessageDeduplicationsCount = 0);
