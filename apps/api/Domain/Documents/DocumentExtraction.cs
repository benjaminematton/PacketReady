using System.Text.Json;

namespace PacketReady.Domain.Documents;

/// <summary>
/// One LLM extraction (or future P5 manual edit) for a <see cref="Document"/>.
/// UPDATE is blocked at the DB level via a BEFORE-UPDATE trigger (see
/// <c>AddDocumentStore</c> migration); row lifetime tracks the parent provider
/// via cascade FK, so "append-only" means in-place mutation is rejected, not that
/// rows survive provider deletion. The latest row per
/// <c>(document_id, schema_version)</c> wins — older rows persist for audit.
///
/// <para><see cref="ExtractionId"/> is the human-meaningful identifier ("extraction
/// #2 of document X"). Allocator lives in the upload handler (spec §"Why per-document
/// extraction_id"); the handler MUST take <c>pg_advisory_xact_lock(hashtext(document_id::text))</c>
/// before computing <c>COALESCE(MAX(extraction_id), 0) + 1</c>. The
/// <c>UNIQUE (document_id, extraction_id)</c> constraint catches any caller that
/// skips the lock — concurrent inserts surface as a duplicate-key exception the
/// handler retries.</para>
///
/// <para>Idempotency belt: the <c>UNIQUE (document_id, schema_version, model,
/// prompt_hash)</c> constraint dedups LLM rows. Postgres treats NULLs as distinct,
/// so manual-edit rows (model = NULL) are never deduped — by design.</para>
/// </summary>
public class DocumentExtraction
{
    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public int ExtractionId { get; private set; }
    public string SchemaVersion { get; private set; } = null!;
    public ExtractionStatus Status { get; private set; }

    /// <summary>camelCase JSONB; <c>{}</c> on <see cref="ExtractionStatus.Failed"/>.</summary>
    public string FieldsJson { get; private set; } = "{}";

    /// <summary><c>{ field: { page, bbox: [x,y,w,h] } }</c>; <c>{}</c> on Failed.</summary>
    public string FieldLocationsJson { get; private set; } = "{}";

    /// <summary>
    /// <c>{ field: 0.00–1.00 }</c>; missing key defaults to 0.0; <c>{}</c> on Failed.
    /// P4 readers MUST go through a single accessor (TBD <c>ConfidenceLookup</c>
    /// value object) so the "missing key = 0.0" rule lives in one place, not
    /// duplicated across every validator. Treating absence as 1.0 silently upgrades
    /// unknowns to passing — fail loud on uncertainty.
    /// </summary>
    public string ConfidenceJson { get; private set; } = "{}";

    /// <summary>Non-null iff <see cref="Status"/> is <see cref="ExtractionStatus.Failed"/>.</summary>
    public string? Error { get; private set; }

    public ExtractionSource Source { get; private set; }

    /// <summary>Non-null iff <see cref="Source"/> ≠ <see cref="ExtractionSource.Llm"/>. P5.</summary>
    public Guid? EditedBy { get; private set; }

    /// <summary>Non-null iff <see cref="Source"/> = <see cref="ExtractionSource.Llm"/>.</summary>
    public string? Model { get; private set; }
    public string? PromptHash { get; private set; }
    public int? InputTokens { get; private set; }
    public int? OutputTokens { get; private set; }

    public DateTimeOffset ExtractedAt { get; private set; }

    /// <summary>
    /// Set when the row is confirmed for downstream consumption. P3 LLM rows are
    /// auto-confirmed at write time (set to <c>ExtractedAt</c>). P5 manual-edit
    /// rows land null until the provider/admin explicitly confirms.
    /// </summary>
    public DateTimeOffset? ConfirmedAt { get; private set; }

    private DocumentExtraction() { }

    /// <summary>
    /// Successful LLM extraction. All field-shape and provenance invariants enforced
    /// in one place: callers can't construct a Succeeded row without populating the
    /// JSON triple and the LLM provenance quartet.
    /// </summary>
    public static DocumentExtraction CreateLlmSucceeded(
        Guid documentId,
        int extractionId,
        string schemaVersion,
        string fieldsJson,
        string fieldLocationsJson,
        string confidenceJson,
        string model,
        string promptHash,
        int inputTokens,
        int outputTokens,
        DateTimeOffset? now = null)
    {
        ValidateCommon(documentId, extractionId, schemaVersion);
        ValidateLlmProvenance(model, promptHash, inputTokens, outputTokens);
        ValidateJsonObject(fieldsJson, nameof(fieldsJson));
        ValidateJsonObject(fieldLocationsJson, nameof(fieldLocationsJson));
        ValidateJsonObject(confidenceJson, nameof(confidenceJson));

        var extractedAt = now ?? DateTimeOffset.UtcNow;

        return new DocumentExtraction
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            ExtractionId = extractionId,
            SchemaVersion = schemaVersion,
            Status = ExtractionStatus.Succeeded,
            FieldsJson = fieldsJson,
            FieldLocationsJson = fieldLocationsJson,
            ConfidenceJson = confidenceJson,
            Error = null,
            Source = ExtractionSource.Llm,
            EditedBy = null,
            Model = model,
            PromptHash = promptHash,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ExtractedAt = extractedAt,
            ConfirmedAt = extractedAt,
        };
    }

    /// <summary>
    /// Failed LLM extraction. Persists the row with empty JSON triple and the
    /// failure reason, so the aggregator can emit an Extraction-Failed Issue
    /// instead of treating the document as missing. Token counts can be partial
    /// (input set, output unset) when the model started but didn't complete; pass
    /// nulls when unknown.
    /// </summary>
    public static DocumentExtraction CreateLlmFailed(
        Guid documentId,
        int extractionId,
        string schemaVersion,
        string error,
        string model,
        string promptHash,
        int? inputTokens = null,
        int? outputTokens = null,
        DateTimeOffset? now = null)
    {
        ValidateCommon(documentId, extractionId, schemaVersion);
        ValidateLlmProvenance(model, promptHash, inputTokens, outputTokens);

        if (string.IsNullOrWhiteSpace(error))
            throw new ArgumentException("Error message is required on Failed rows.", nameof(error));

        // Failed rows are not auto-confirmed: the aggregator filters on
        // status='Succeeded' anyway, but leaving confirmed_at null keeps the
        // "downstream-readable" semantic honest.
        return new DocumentExtraction
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            ExtractionId = extractionId,
            SchemaVersion = schemaVersion,
            Status = ExtractionStatus.Failed,
            FieldsJson = "{}",
            FieldLocationsJson = "{}",
            ConfidenceJson = "{}",
            Error = error,
            Source = ExtractionSource.Llm,
            EditedBy = null,
            Model = model,
            PromptHash = promptHash,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ExtractedAt = now ?? DateTimeOffset.UtcNow,
            ConfirmedAt = null,
        };
    }

    private static void ValidateCommon(Guid documentId, int extractionId, string schemaVersion)
    {
        if (documentId == Guid.Empty)
            throw new ArgumentException("Document id is required.", nameof(documentId));
        if (extractionId < 1)
            throw new ArgumentOutOfRangeException(
                nameof(extractionId), extractionId, "Extraction id is 1-indexed.");
        if (string.IsNullOrWhiteSpace(schemaVersion))
            throw new ArgumentException("Schema version is required.", nameof(schemaVersion));
    }

    private static void ValidateLlmProvenance(string model, string promptHash, int? inputTokens, int? outputTokens)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required on LLM rows.", nameof(model));
        if (string.IsNullOrWhiteSpace(promptHash))
            throw new ArgumentException("Prompt hash is required on LLM rows.", nameof(promptHash));
        if (inputTokens is < 0)
            throw new ArgumentOutOfRangeException(nameof(inputTokens), inputTokens, "Token count must be >= 0.");
        if (outputTokens is < 0)
            throw new ArgumentOutOfRangeException(nameof(outputTokens), outputTokens, "Token count must be >= 0.");
    }

    // Spec calls for JSON OBJECTS (`{ field: ... }`, `{ field: 0.97 }`). Postgres'
    // jsonb column accepts scalars too, so without this check a row could land with
    // `fields = "null"` or `fields = 42` and the aggregator would crash deserializing.
    private static void ValidateJsonObject(string json, string paramName)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("JSON payload is required.", paramName);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"Payload must be valid JSON. Got: {json[..Math.Min(json.Length, 80)]}",
                paramName, ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException(
                    $"Payload must be a JSON object. Got kind: {doc.RootElement.ValueKind}.",
                    paramName);
        }
    }
}
