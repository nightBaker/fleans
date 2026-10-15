namespace Fleans.Domain;

/// <summary>
/// Validation rules for a process instance's optional business identifier (#824).
/// Mirrors Camunda 8.9+'s <c>businessId</c>: optional, immutable, bounded length.
/// </summary>
public static class BusinessIdRules
{
    public const int MaxLength = 256;

    /// <summary>
    /// Throws <see cref="ArgumentException"/> (HTTP 400 at the API) when
    /// <paramref name="businessId"/> is blank, padded with whitespace, or too long. <c>null</c> means
    /// "no business id". Padding is rejected rather than trimmed, so the stored id is always exactly
    /// what the caller sent and exact-match lookups find it.
    /// </summary>
    public static void Validate(string? businessId)
    {
        if (businessId is null)
            return;
        if (string.IsNullOrWhiteSpace(businessId))
            throw new ArgumentException("BusinessId must not be empty or whitespace when provided.", nameof(businessId));
        if (char.IsWhiteSpace(businessId[0]) || char.IsWhiteSpace(businessId[^1]))
            throw new ArgumentException("BusinessId must not start or end with whitespace.", nameof(businessId));
        if (businessId.Length > MaxLength)
            throw new ArgumentException(
                $"BusinessId must be at most {MaxLength} characters (got {businessId.Length}).", nameof(businessId));
    }
}
