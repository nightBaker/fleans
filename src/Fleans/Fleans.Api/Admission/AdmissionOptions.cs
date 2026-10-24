namespace Fleans.Api.Admission;

/// <summary>
/// <c>Fleans:Admission</c> — the adaptive concurrency limit on the REST mutation endpoints (#922).
/// See <see cref="AdaptiveConcurrencyLimiter"/> for the algorithm and
/// <c>website/src/content/docs/reference/configuration.md</c> for the operator view.
/// </summary>
public sealed class AdmissionOptions
{
    public const string SectionName = "Fleans:Admission";

    /// <summary>On by default: it only moves when the latency target is missed (see the configuration reference).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>p95 request latency (ms) above which the limit shrinks.</summary>
    public int TargetLatencyMs { get; set; } = 500;

    /// <summary>Concurrent admitted requests per process at startup.</summary>
    public int InitialLimit { get; set; } = 64;

    /// <summary>The limit never shrinks below this, so a light client never sees a 429.</summary>
    public int MinLimit { get; set; } = 8;

    /// <summary>The limit never grows above this.</summary>
    public int MaxLimit { get; set; } = 1024;

    /// <summary>Length (ms) of the window whose p95 decides each adjustment.</summary>
    public int WindowMs { get; set; } = 1000;

    /// <summary>Startup check: the reason the options are invalid, or <c>null</c>.</summary>
    public string? Validate()
    {
        if (TargetLatencyMs < 1) return $"{SectionName}:TargetLatencyMs must be >= 1 (got {TargetLatencyMs}).";
        if (WindowMs < 1) return $"{SectionName}:WindowMs must be >= 1 (got {WindowMs}).";
        if (MinLimit < 1) return $"{SectionName}:MinLimit must be >= 1 (got {MinLimit}).";
        if (MaxLimit < MinLimit) return $"{SectionName}:MaxLimit ({MaxLimit}) must be >= MinLimit ({MinLimit}).";
        if (InitialLimit < MinLimit || InitialLimit > MaxLimit)
            return $"{SectionName}:InitialLimit ({InitialLimit}) must be between MinLimit ({MinLimit}) and MaxLimit ({MaxLimit}).";
        return null;
    }
}
