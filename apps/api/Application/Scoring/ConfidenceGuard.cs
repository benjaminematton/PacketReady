using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring;

/// <summary>
/// Post-processing pass that downgrades a <see cref="Severity.Critical"/>
/// <see cref="Issue"/> to <see cref="Severity.Minor"/> when at least one of
/// its citations is flagged <see cref="Citation.LowConfidence"/>. Pure fold
/// over the issue list — no DB access, no LLM call, no provenance lookup.
///
/// <para><b>How the flag gets there:</b>
/// <see cref="Providers.Aggregation.ProvenanceExtensions.Cite"/> stamps
/// <c>LowConfidence = (FieldProvenance.Confidence &lt; CriticalEligibleThreshold)</c>
/// at citation construction. LLM validators that build <see cref="Citation"/>
/// directly (e.g. IdentityCoherence) call the same helper, so they participate
/// in the gate automatically. A validator that constructs a Citation
/// outside the helper bypasses the guard — there are no such call sites in
/// P4, but anyone adding one needs to set the flag themselves.</para>
///
/// <para><b>Why a downgrade and not a drop:</b> a low-confidence input that
/// looks Critical is still a real signal — the dashboard wants to surface it
/// — but it shouldn't gate readiness like a high-confidence Critical. The
/// downgraded Issue carries <see cref="Issue.IsLowConfidenceInput"/> = true
/// so the side-panel can render "downgraded from Critical due to
/// low-confidence input" in plain language.</para>
///
/// <para><b>Idempotent:</b> a second pass over an already-downgraded list is
/// a no-op. The downgraded Issue is <see cref="Severity.Minor"/>, which doesn't
/// match the <c>Critical</c> precondition.</para>
/// </summary>
public static class ConfidenceGuard
{
    /// <summary>
    /// Threshold below which a cited field's confidence renders its parent
    /// Critical Issue ineligible to remain Critical. Inherited from
    /// design.md §11.1. Revisit only with data showing it's wrong.
    /// </summary>
    public const double CriticalEligibleThreshold = 0.85;

    public static IReadOnlyList<Issue> Apply(IReadOnlyList<Issue> issues)
    {
        if (issues.Count == 0) return issues;
        return issues.Select(Downgrade).ToList();
    }

    private static Issue Downgrade(Issue i) =>
        i.Severity == Severity.Critical && i.Citations.Any(c => c.LowConfidence)
            ? i with { Severity = Severity.Minor, IsLowConfidenceInput = true }
            : i;
}
