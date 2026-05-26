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

    /// <summary>
    /// Discriminator used by P4 LLM validators (<c>identity_coherence</c>,
    /// <c>npi_taxonomy_match</c>) to name the specific field a finding is
    /// about — e.g. <c>"malpractice.fullName"</c> or
    /// <c>"boardCert.specialty"</c>. The eval runner's
    /// <c>conflict_metrics.py</c> uses this (predicate 3) to distinguish
    /// "right validator, wrong finding" from a real catch. Empty string is
    /// the unset sentinel; pure-code validators leave it unset because
    /// their citations already pin the field via <c>provenanceKey</c>.
    /// </summary>
    public string Field { get; init; } = "";
}
