namespace Fleans.Domain;

/// <summary>
/// Result of escalation handling in the parent workflow.
/// Returned by OnChildEscalationRaised to tell the child grain what to do next.
/// </summary>
[GenerateSerializer]
public enum EscalationHandledResult
{
    /// <summary>Child continues execution normally. Boundary was non-interrupting or escalation was uncaught.</summary>
    Continue,

    /// <summary>Child scope was cancelled by an interrupting boundary. Child must not advance to outgoing flow.</summary>
    Cancelled,

    /// <summary>No boundary found in this grain's scope; the parent grain must be consulted (CallActivity escape path).</summary>
    NeedsParentLookup,

    /// <summary>
    /// Escalation propagated through the entire grain chain and was not caught by any boundary.
    /// Semantically equivalent to Continue for the originating child — execution proceeds normally.
    /// </summary>
    Unhandled,
}
