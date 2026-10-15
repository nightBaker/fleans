namespace Fleans.Domain;

/// <summary>
/// Validation rules for a process instance's optional business identifier (#824).
/// Mirrors Camunda 8.9+'s <c>businessId</c>: optional, immutable, bounded length.
/// </summary>
public static class BusinessIdRules
{
    public const int MaxLength = 256;

    /// <summary>
    /// Returns an error message when <paramref name="businessId"/> is invalid, otherwise
    /// <c>null</c>. A <c>null</c> business id is valid (the instance has none).
    /// </summary>
    public static string? GetValidationError(string? businessId)
    {
        if (businessId is null)
            return null;
        if (string.IsNullOrWhiteSpace(businessId))
            return "BusinessId must not be empty or whitespace when provided.";
        if (businessId.Length > MaxLength)
            return $"BusinessId must be at most {MaxLength} characters (got {businessId.Length}).";
        return null;
    }

    public static void Validate(string? businessId)
    {
        var error = GetValidationError(businessId);
        if (error is not null)
            throw new ArgumentException(error, nameof(businessId));
    }
}
