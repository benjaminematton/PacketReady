namespace PacketReady.Domain.Documents;

/// <summary>
/// One uploaded PDF. Carries the blob-store URI plus classifier provenance
/// (model + prompt hash + self-reported confidence). One <see cref="Document"/>
/// has 1..N <c>DocumentExtraction</c> rows; the latest per <c>schema_version</c>
/// is what the aggregator reads.
///
/// <para>Schema lives in the <c>documents</c> table; spec at
/// <c>docs/impl/phase-3-extractors.md §"Document store schema"</c>. <see cref="DocType"/>
/// and <see cref="DocTypeConfidence"/> are nullable because the classifier failure mode
/// (network error, rate limit) persists the row without a classification; the aggregator
/// emits a Critical Issue when this happens.</para>
/// </summary>
public class Document
{
    public Guid Id { get; private set; }
    public Guid ProviderId { get; private set; }

    /// <summary>Nullable: classifier failure leaves <c>doc_type</c> unset.</summary>
    public DocType? DocType { get; private set; }

    /// <summary>Nullable: paired with <see cref="DocType"/>. 0.00–1.00 when set.</summary>
    public double? DocTypeConfidence { get; private set; }

    public string ClassifierModel { get; private set; } = null!;
    public string ClassifierPromptHash { get; private set; } = null!;

    /// <summary><c>file:///…</c> in P3; <c>s3://…</c> in P6.</summary>
    public string StorageUri { get; private set; } = null!;

    public string OriginalName { get; private set; } = null!;
    public string MimeType { get; private set; } = null!;
    public int PageCount { get; private set; }
    public DateTimeOffset UploadedAt { get; private set; }
    public Uploader UploadedBy { get; private set; }

    private Document() { }

    public static Document Create(
        Guid providerId,
        DocType? docType,
        double? docTypeConfidence,
        string classifierModel,
        string classifierPromptHash,
        string storageUri,
        string originalName,
        string mimeType,
        int pageCount,
        Uploader uploadedBy,
        DateTimeOffset? now = null)
    {
        if (providerId == Guid.Empty)
            throw new ArgumentException("Provider id is required.", nameof(providerId));
        if (string.IsNullOrWhiteSpace(classifierModel))
            throw new ArgumentException("Classifier model is required.", nameof(classifierModel));
        if (string.IsNullOrWhiteSpace(classifierPromptHash))
            throw new ArgumentException("Classifier prompt hash is required.", nameof(classifierPromptHash));
        if (string.IsNullOrWhiteSpace(storageUri))
            throw new ArgumentException("Storage URI is required.", nameof(storageUri));
        if (string.IsNullOrWhiteSpace(originalName))
            throw new ArgumentException("Original name is required.", nameof(originalName));
        if (string.IsNullOrWhiteSpace(mimeType))
            throw new ArgumentException("MIME type is required.", nameof(mimeType));
        if (pageCount < 1)
            throw new ArgumentOutOfRangeException(nameof(pageCount), pageCount, "Page count must be >= 1.");

        // doc_type and doc_type_conf are paired: either both set or both null.
        // Mixed nullability would let a row claim "I have a classification but no
        // confidence in it" or vice versa — neither is meaningful.
        if (docType.HasValue != docTypeConfidence.HasValue)
            throw new ArgumentException(
                "doc_type and doc_type_conf must both be set or both be null.",
                docType.HasValue ? nameof(docTypeConfidence) : nameof(docType));

        if (docTypeConfidence is { } c && c is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(docTypeConfidence), c, "Confidence must be in [0, 1].");

        return new Document
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            DocType = docType,
            DocTypeConfidence = docTypeConfidence,
            ClassifierModel = classifierModel,
            ClassifierPromptHash = classifierPromptHash,
            StorageUri = storageUri,
            OriginalName = originalName,
            MimeType = mimeType,
            PageCount = pageCount,
            UploadedBy = uploadedBy,
            UploadedAt = now ?? DateTimeOffset.UtcNow,
        };
    }
}
