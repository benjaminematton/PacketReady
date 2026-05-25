namespace PacketReady.Domain.Scoring;

/// <summary>
/// One emitted finding from a validator. A single validator may emit zero, one, or
/// many Issues per run — there is no short-circuit. <see cref="Validator"/> matches
/// the emitting <c>IValidator.Name</c> so the side-panel can cross-reference.
///
/// <para><b>Record equality caveat:</b> <see cref="Citations"/> is an
/// <see cref="IReadOnlyList{T}"/>, so record <c>==</c> falls back to reference
/// equality on the list. Issues are compared via JSON round-trip, not in-memory.</para>
/// </summary>
public sealed record Issue(
    string Validator,
    Severity Severity,
    string Message,
    string Remediation,
    IReadOnlyList<Citation> Citations)
{
    /// <summary>
    /// Set true when P4's confidence-threshold gate downgrades a Critical to a
    /// Minor because at least one cited field had extractor confidence &lt; 0.85.
    /// P3 never flips it; the field lands now so the issues-JSONB shape doesn't
    /// churn when P4 ships.
    /// </summary>
    public bool IsLowConfidenceInput { get; init; } = false;
}
