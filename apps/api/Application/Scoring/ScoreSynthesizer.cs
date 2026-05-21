using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring;

/// <summary>
/// Aggregates a flat list of <see cref="Issue"/>s into a 0..100 score. The rubric is
/// intentionally simple — a weighted sum is easier to defend than a learned score
/// and easier to debug when an admin asks "why is this provider at 73?"
///
/// <para>Tier derivation lives on <see cref="TierExtensions.FromScore"/>, not here —
/// callers compute the score and then derive the tier from it. Keeps the two
/// invariants (score arithmetic and score→tier mapping) decoupled.</para>
///
/// <para><b>Contract:</b> <see cref="Compute"/> returns an integer in <c>[0, 100]</c>.
/// <see cref="ReadinessScore.Create"/> re-validates this range as defense-in-depth;
/// changes here that violate the contract must update that guard too.</para>
/// </summary>
public static class ScoreSynthesizer
{
    // Public so tests and future per-payer overrides can reference one source of
    // truth. Eval data may revise these weights; tests pin the current values to
    // catch unintentional drift.
    public const int CriticalPenalty = 25;
    public const int MajorPenalty = 10;
    public const int MinorPenalty = 3;

    public static int Compute(IReadOnlyList<Issue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        var score = 100;
        foreach (var i in issues)
        {
            score -= i.Severity switch
            {
                Severity.Critical => CriticalPenalty,
                Severity.Major => MajorPenalty,
                Severity.Minor => MinorPenalty,
                // Unknown severity from a corrupted JSONB row or a future enum value
                // that wasn't wired in here. Fail loud rather than scoring it free.
                _ => throw new ArgumentOutOfRangeException(
                    nameof(issues), i.Severity, $"Unknown severity: {i.Severity}"),
            };
        }

        // Floor at 0; the rubric is intentionally one-directional (issues only subtract),
        // so anything below 0 collapses to "unsubmittable" — no useful information in
        // distinguishing "very bad" from "extremely bad."
        return Math.Max(0, score);
    }
}
