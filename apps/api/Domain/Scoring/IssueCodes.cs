namespace PacketReady.Domain.Scoring;

/// <summary>
/// Stable code constants stamped on <see cref="Issue.Code"/> by the aggregator
/// (and any future emitter that wants to participate in tag-based routing).
/// Code strings are part of the persisted issues-JSONB shape — bumping a value
/// here is a breaking change for any downstream consumer (dashboard filters,
/// audit-table queries). Add a new constant rather than renaming an existing
/// one.
/// </summary>
public static class IssueCodes
{
    /// <summary>
    /// Aggregator could not find a document of a required doc type for the
    /// provider. Paired with <see cref="Issue.MissingDocType"/> set to the
    /// doc type that's absent. The score-compute handler may suppress
    /// <c>(MissingDocument, BoardCert)</c> when the resolved payer doesn't
    /// require a board cert.
    /// </summary>
    public const string MissingDocument = "missing_document";

    /// <summary>
    /// The latest extraction row for the document is in
    /// <c>ExtractionStatus.Failed</c> (or never landed at all).
    /// </summary>
    public const string ExtractionFailed = "extraction_failed";

    /// <summary>
    /// Extraction landed in <c>Succeeded</c> but one or more required fields
    /// came back null — the typed record couldn't be constructed.
    /// </summary>
    public const string PartialExtraction = "partial_extraction";

    /// <summary>
    /// Classifier landed the doc type in the 0.50–0.85 mid-band per spec
    /// §"Classifier confidence bands".
    /// </summary>
    public const string LowConfidenceClassification = "low_confidence_classification";

    /// <summary>
    /// Cross-doc <c>fullName</c> mismatch by Levenshtein ≥ 3 after credential
    /// suffix normalization — see <c>ProviderProfileAggregator.ResolveFullName</c>.
    /// </summary>
    public const string CrossDocNameMismatch = "cross_doc_name_mismatch";
}

/// <summary>
/// Spec for the <see cref="Issue.Field"/> discriminator. Pinned cross-language
/// so the Python conflict-metrics runner and the C# validators agree on the
/// exact byte sequence. <c>conflict_metrics.py</c> imports <c>FIELD_SEP</c> as
/// a sibling constant; the two stay in lockstep via
/// <c>FieldSeparatorContractTests</c> (reads the Python constant by string
/// match — both ends fail the build if they drift).
/// </summary>
public static class IssueFieldSpec
{
    /// <summary>Separator between the docType label and the field name.</summary>
    public const char FieldSeparator = '.';

    /// <summary>
    /// Compose a discriminator value, e.g.
    /// <c>Format("malpractice", "fullName") == "malpractice.fullName"</c>.
    /// Both inputs must be non-empty; we throw rather than emit a malformed
    /// discriminator that would silently miss conflict-metrics matches.
    /// </summary>
    public static string Format(string docType, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(docType))
            throw new ArgumentException("docType is required.", nameof(docType));
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new ArgumentException("fieldName is required.", nameof(fieldName));
        return $"{docType}{FieldSeparator}{fieldName}";
    }
}
