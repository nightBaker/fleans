namespace Fleans.Application.ExternalTasks;

/// <summary>
/// The opaque <c>task_token</c> of a <c>TaskAssignment</c>: the activity instance (which names
/// the <see cref="IExternalTaskDispatcherGrain"/> waiting for the outcome) and the id of this
/// delivery attempt, as 64 hex digits. A fresh dispatch id per attempt keeps a late report of an
/// abandoned attempt from settling its redelivery.
/// </summary>
internal static class ExternalTaskToken
{
    public static string Format(Guid activityInstanceId, Guid dispatchId) =>
        $"{activityInstanceId:N}{dispatchId:N}";

    public static bool TryParse(string? token, out Guid activityInstanceId, out Guid dispatchId)
    {
        activityInstanceId = default;
        dispatchId = default;
        return token is { Length: 64 }
            && Guid.TryParseExact(token.AsSpan(0, 32), "N", out activityInstanceId)
            && Guid.TryParseExact(token.AsSpan(32), "N", out dispatchId);
    }
}
