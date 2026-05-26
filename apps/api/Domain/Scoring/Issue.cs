using PacketReady.Domain.Documents;

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
    /// <c>"boardCert.specialty"</c>. Format is locked to
    /// <c>"&lt;docType&gt;&lt;FieldSeparator&gt;&lt;fieldName&gt;"</c>; see
    /// <see cref="IssueFieldSpec"/>. The eval runner's
    /// <c>conflict_metrics.py</c> uses this (predicate 3) to distinguish
    /// "right validator, wrong finding" from a real catch. Empty string is
    /// the unset sentinel; pure-code validators leave it unset because
    /// their citations already pin the field via <c>provenanceKey</c>.
    /// </summary>
    public string Field { get; init; } = "";

    /// <summary>
    /// Stable code identifying the kind of finding (e.g.
    /// <c>"missing_document"</c>, <c>"extraction_failed"</c>) — see
    /// <see cref="IssueCodes"/>. Set by the aggregator on its emitted Issues
    /// so the handler can suppress / re-route by tag instead of by message
    /// substring. Validators leave it empty; the validator name plus
    /// <see cref="Field"/> already discriminate finely enough for them.
    /// </summary>
    public string Code { get; init; } = "";

    /// <summary>
    /// Doc-type tag for aggregator-emitted Issues that point at a specific
    /// uploaded document (missing-document, extraction-failed,
    /// partial-extraction, low-confidence-classification). Lets the handler
    /// run payer-config-driven suppression (e.g. drop missing-BoardCert
    /// Critical when payer.BoardCertRequired == false) without re-parsing
    /// the message string. Null on validator-emitted Issues.
    /// </summary>
    public DocType? MissingDocType { get; init; } = null;
}
