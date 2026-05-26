using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Extraction.Extract;
using PacketReady.Application.Llm;
using PacketReady.Application.Prompts;
using PacketReady.Domain.Documents;

namespace PacketReady.Infrastructure.Extraction.SonnetExtractors;

/// <summary>
/// Shared body of the four Sonnet-backed extractors. Each subclass supplies
/// <see cref="DocType"/>, <see cref="SchemaVersion"/>, <see cref="PromptResourceName"/>,
/// <see cref="SchemaName"/>, and a <see cref="Fields"/> list — the base class
/// assembles the JSON schema, drives the structured-output call, validates the
/// per-field envelope, and splits the response into the three storage-shape JSONBs.
///
/// <para>The Anthropic SDK is reached through Microsoft.Extensions.AI's
/// <see cref="IChatClient"/>: <c>DataContent</c> with the <c>application/pdf</c>
/// media type maps to Anthropic's document block; <c>ChatOptions.ResponseFormat</c>
/// = <c>ChatResponseFormat.ForJsonSchema</c> forces structured output.</para>
///
/// <para>The schema is built from <see cref="Fields"/> using the Anthropic-accepted
/// subset only: <c>type</c>, <c>properties</c>, <c>required</c>,
/// <c>additionalProperties</c>, <c>items</c>, <c>enum</c>, <c>anyOf</c>. Numeric
/// ranges (<c>minimum</c>/<c>maximum</c>) and array cardinality
/// (<c>minItems</c>/<c>maxItems</c>) are rejected by the server-side validator;
/// those bounds are enforced post-parse in <see cref="ValidateEnvelope"/>.</para>
/// </summary>
internal abstract class SonnetExtractorBase : IDocTypeExtractor
{
    /// <summary>
    /// Model id landing on <c>document_extractions.model</c>. Part of the
    /// idempotency key — bumping it invalidates the cache (spec §"Why the
    /// unique-by-(model, prompt_hash)").
    /// </summary>
    public const string ModelId = "claude-sonnet-4-6";

    private const float Temperature = 0f;

    // 6-field extractions land ~400 tokens; 2 KB covers the four doc types with
    // headroom. Outputs at or above NearCapOutputTokens are likely truncated —
    // SplitLlmResponse attaches that context to JsonException re-throws.
    private const int MaxOutputTokens = 2048;
    private const int NearCapOutputTokens = (int)(MaxOutputTokens * 0.9);

    private readonly IChatClient _chat;
    private readonly IPromptLoader _prompts;
    private readonly PromptHasher _hasher;
    private readonly ILogger _logger;

    // Schema is composed from the constant `Fields` list once per extractor
    // instance; reused across every ExtractAsync call. JsonDocument is thread-
    // safe for read after parse, so a single doc per extractor is sufficient.
    private readonly Lazy<JsonDocument> _schemaDoc;

    public abstract DocType DocType { get; }
    public abstract string SchemaVersion { get; }
    public abstract string PromptResourceName { get; }

    // Shared across all four P3 Sonnet extractors. If a future extractor wants
    // to override (e.g. Sonnet 4.7 for a single doc type during a rollout),
    // make this virtual — for now `sealed` matches the spec lock.
    public string Model => ModelId;

    /// <summary>
    /// Human-friendly schema name passed to the Anthropic adapter. Used as a
    /// tool-name hint when MS.Extensions.AI synthesizes a forced tool call to
    /// enforce structured output.
    /// </summary>
    protected abstract string SchemaName { get; }

    /// <summary>
    /// Ordered list of fields the extractor produces. The base class generates
    /// the JSON schema and the <c>required</c> arrays from this list.
    /// </summary>
    protected abstract IReadOnlyList<FieldSpec> Fields { get; }

    protected SonnetExtractorBase(
        IChatClient chat,
        IPromptLoader prompts,
        PromptHasher hasher,
        ILogger logger)
    {
        _chat = chat;
        _prompts = prompts;
        _hasher = hasher;
        _logger = logger;
        _schemaDoc = new Lazy<JsonDocument>(() => JsonDocument.Parse(BuildSchemaJson()));
    }

    public async Task<ExtractionResult> ExtractAsync(ReadOnlyMemory<byte> pdf, CancellationToken ct)
    {
        if (pdf.IsEmpty)
            throw new ArgumentException("PDF bytes were empty.", nameof(pdf));

        var systemPrompt = await _prompts.LoadAsync(PromptResourceName, ct);
        var promptHash = await _hasher.HashOfAsync(PromptResourceName, ct);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, new List<AIContent>
            {
                new DataContent(pdf, "application/pdf"),
                new TextContent("Extract the fields per the system instructions and the response schema."),
            }),
        };

        var options = new ChatOptions
        {
            ModelId = ModelId,
            Temperature = Temperature,
            MaxOutputTokens = MaxOutputTokens,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                _schemaDoc.Value.RootElement,
                schemaName: SchemaName,
                schemaDescription: $"Structured output for {DocType} extraction ({SchemaVersion})."),
        };

        var response = await _chat.GetResponseAsync(messages, options, ct);

        var rawJson = ChatResponseParser.ExtractStructuredJson(response);
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new ExtractorResponseException(
                $"Extractor {DocType} returned no usable JSON in response (neither FunctionCallContent nor Text).");

        var inputTokens = (int)(response.Usage?.InputTokenCount ?? 0);
        var outputTokens = (int)(response.Usage?.OutputTokenCount ?? 0);

        var (fieldsJson, locationsJson, confidenceJson) = SplitLlmResponse(rawJson, outputTokens);

        _logger.LogInformation(
            "Sonnet extraction completed: docType={DocType}, schema={SchemaVersion}, in={InTokens}, out={OutTokens}",
            DocType, SchemaVersion, inputTokens, outputTokens);

        return new ExtractionResult(
            FieldsJson: fieldsJson,
            FieldLocationsJson: locationsJson,
            ConfidenceJson: confidenceJson,
            Model: ModelId,
            PromptHash: promptHash,
            InputTokens: inputTokens,
            OutputTokens: outputTokens);
    }

    /// <summary>
    /// Splits Sonnet's combined response into the three storage-shape JSONBs
    /// and validates the per-field envelope. Sonnet returns
    /// <c>{ fields: { k: { value, page, bbox } }, confidence: { k: x } }</c>;
    /// the DB has separate columns for values, locations, and confidences.
    ///
    /// <para><paramref name="outputTokens"/> is used only to enrich the error
    /// when the response is unparseable JSON — counts at or above
    /// <c>NearCapOutputTokens</c> indicate likely truncation, which the
    /// operator should resolve by raising the cap, not by retrying the
    /// prompt.</para>
    /// </summary>
    internal static (string Fields, string Locations, string Confidence) SplitLlmResponse(
        string rawJson, int outputTokens = 0)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            var hint = outputTokens >= NearCapOutputTokens
                ? $" (output tokens {outputTokens} ≥ {NearCapOutputTokens}/{MaxOutputTokens} — likely truncated)"
                : "";
            throw new ExtractorResponseException(
                $"Extractor response was not valid JSON{hint}: {ChatResponseParser.TruncateForError(rawJson)}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ExtractorResponseException(
                    $"Extractor response was not a JSON object (kind={root.ValueKind}).");

            if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                throw new ExtractorResponseException("Extractor response missing object 'fields'.");
            if (!root.TryGetProperty("confidence", out var confidence) || confidence.ValueKind != JsonValueKind.Object)
                throw new ExtractorResponseException("Extractor response missing object 'confidence'.");

            ValidateEnvelope(fields, confidence);

            return (
                WriteToString(w =>
                {
                    w.WriteStartObject();
                    foreach (var prop in fields.EnumerateObject())
                    {
                        w.WritePropertyName(prop.Name);
                        prop.Value.GetProperty("value").WriteTo(w);
                    }
                    w.WriteEndObject();
                }),
                WriteToString(w =>
                {
                    w.WriteStartObject();
                    foreach (var prop in fields.EnumerateObject())
                    {
                        w.WritePropertyName(prop.Name);
                        w.WriteStartObject();
                        w.WritePropertyName("page");
                        prop.Value.GetProperty("page").WriteTo(w);
                        w.WritePropertyName("bbox");
                        prop.Value.GetProperty("bbox").WriteTo(w);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }),
                confidence.GetRawText()
            );
        }
    }

    /// <summary>
    /// Post-parse bounds check for the constraints the Anthropic schema subset
    /// can't express: <c>page ≥ 1</c>, <c>bbox</c> is exactly 4 finite numbers,
    /// and each confidence value sits in <c>[0, 1]</c>. The prompts ask for
    /// these — Sonnet honors them at t=0 — but a silent drift would otherwise
    /// land in the <c>field_locations</c> / <c>confidence</c> JSONB columns
    /// and corrupt downstream consumers (highlight overlay, score gating).
    /// </summary>
    private static void ValidateEnvelope(JsonElement fields, JsonElement confidence)
    {
        foreach (var prop in fields.EnumerateObject())
        {
            var envelope = prop.Value;
            if (envelope.ValueKind != JsonValueKind.Object)
                throw new ExtractorResponseException(
                    $"Field '{prop.Name}' envelope is not an object (kind={envelope.ValueKind}).");

            if (!envelope.TryGetProperty("value", out _))
                throw new ExtractorResponseException(
                    $"Field '{prop.Name}' missing 'value' in extractor response.");

            if (!envelope.TryGetProperty("page", out var pageEl))
                throw new ExtractorResponseException(
                    $"Field '{prop.Name}' missing 'page' in extractor response.");
            if (pageEl.ValueKind != JsonValueKind.Number || !pageEl.TryGetInt32(out var page) || page < 1)
                throw new ExtractorResponseException(
                    $"Field '{prop.Name}' has invalid 'page' (must be integer ≥ 1, got {pageEl.GetRawText()}).");

            if (!envelope.TryGetProperty("bbox", out var bboxEl))
                throw new ExtractorResponseException(
                    $"Field '{prop.Name}' missing 'bbox' in extractor response.");
            if (bboxEl.ValueKind != JsonValueKind.Array || bboxEl.GetArrayLength() != 4)
                throw new ExtractorResponseException(
                    $"Field '{prop.Name}' has invalid 'bbox' (must be array of 4 numbers, got {bboxEl.GetRawText()}).");
            foreach (var coord in bboxEl.EnumerateArray())
            {
                if (coord.ValueKind != JsonValueKind.Number ||
                    !coord.TryGetDouble(out var n) ||
                    double.IsNaN(n) || double.IsInfinity(n))
                    throw new ExtractorResponseException(
                        $"Field '{prop.Name}' has non-finite 'bbox' coordinate (got {coord.GetRawText()}).");
            }
        }

        foreach (var prop in confidence.EnumerateObject())
        {
            var c = prop.Value;
            if (c.ValueKind != JsonValueKind.Number ||
                !c.TryGetDouble(out var score) ||
                double.IsNaN(score) || double.IsInfinity(score) ||
                score < 0.0 || score > 1.0)
                throw new ExtractorResponseException(
                    $"Field '{prop.Name}' has invalid 'confidence' (must be number in [0, 1], got {c.GetRawText()}).");
        }
    }

    /// <summary>
    /// Renders the wrapping object + value-envelope + confidence schemas from
    /// the subclass's <see cref="Fields"/> list. The wrapping object is
    /// constant across extractors; only the field set varies.
    /// <para><c>internal</c> so the Tests project can snapshot the generated
    /// schema and confirm it stays in the Anthropic-accepted subset.</para>
    /// </summary>
    internal string BuildSchemaJson()
    {
        if (Fields.Count == 0)
            throw new InvalidOperationException(
                $"Extractor {GetType().Name} declared no fields.");

        var requiredList = string.Join(", ", Fields.Select(f => $"\"{f.Name}\""));

        var fieldEntries = string.Join(",\n", Fields.Select(f => $$"""
                "{{f.Name}}": {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["value", "page", "bbox"],
                  "properties": {
                    "value": {{f.ValueSchemaJson}},
                    "page":  { "type": "integer" },
                    "bbox":  { "type": "array", "items": { "type": "number" } }
                  }
                }
        """));

        var confidenceEntries = string.Join(",\n", Fields.Select(f =>
            $$"""            "{{f.Name}}": { "type": "number" }"""));

        return $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["fields", "confidence"],
          "properties": {
            "fields": {
              "type": "object",
              "additionalProperties": false,
              "required": [{{requiredList}}],
              "properties": {
        {{fieldEntries}}
              }
            },
            "confidence": {
              "type": "object",
              "additionalProperties": false,
              "required": [{{requiredList}}],
              "properties": {
        {{confidenceEntries}}
              }
            }
          }
        }
        """;
    }

    private static string WriteToString(Action<Utf8JsonWriter> body)
    {
        var buf = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf))
        {
            body(w);
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

}

/// <summary>
/// One field in an extractor's output: the field's name as the LLM returns it,
/// plus the JSON-schema fragment for its <c>value</c> slot. The wrapping
/// envelope (<c>value</c>/<c>page</c>/<c>bbox</c>) and the confidence schema
/// are added by <see cref="SonnetExtractorBase"/>.
/// </summary>
internal sealed record FieldSpec(string Name, string ValueSchemaJson);

/// <summary>
/// Pre-built value-schema fragments for the kinds of fields the P3 extractors
/// produce. All variants accept <c>null</c> for the missing-field case.
/// </summary>
internal static class FieldValueSchemas
{
    /// <summary>String value, or <c>null</c> when the field is absent.</summary>
    public const string NullableString =
        """{ "anyOf": [ { "type": "string" }, { "type": "null" } ] }""";

    /// <summary>
    /// Array of strings drawn from a fixed enum, or <c>null</c> when absent.
    /// Used for DEA's <c>schedules</c> field.
    /// </summary>
    public static string NullableStringEnumArray(params string[] enumValues)
    {
        var enumList = string.Join(", ", enumValues.Select(v => $"\"{v}\""));
        return $$"""
        {
          "anyOf": [
            { "type": "array", "items": { "type": "string", "enum": [{{enumList}}] } },
            { "type": "null" }
          ]
        }
        """;
    }
}

public sealed class ExtractorResponseException : Exception
{
    public ExtractorResponseException(string message) : base(message) { }
    public ExtractorResponseException(string message, Exception inner) : base(message, inner) { }
}
