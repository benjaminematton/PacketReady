namespace PacketReady.Application.Extraction.Extract;

/// <summary>
/// Storage-shape extractor output. Three JSONB payloads (matching the three
/// <c>document_extractions</c> JSONB columns) plus the LLM provenance quartet.
///
/// <para><c>FieldsJson</c> is already value-only — the per-field <c>{ value, page, bbox }</c>
/// envelope returned by Sonnet has been split: <c>field_locations</c> carries the
/// page/bbox pair, <c>confidence</c> the per-field score, and <c>fields</c> just
/// the values. Path A returns <c>FieldsJson</c> directly under <c>{ "fields": … }</c>;
/// Path B persists all three to the row.</para>
/// </summary>
public sealed record ExtractionResult(
    string FieldsJson,
    string FieldLocationsJson,
    string ConfidenceJson,
    string Model,
    string PromptHash,
    int InputTokens,
    int OutputTokens);
