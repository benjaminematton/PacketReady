using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Extraction.Extract;
using PacketReady.Application.Prompts;
using PacketReady.Domain.Documents;

namespace PacketReady.Infrastructure.Extraction.SonnetExtractors;

/// <summary>
/// Shared body of the four Sonnet-backed extractors. Each subclass supplies
/// <see cref="DocType"/>, <see cref="SchemaVersion"/>, <see cref="PromptResourceName"/>,
/// and the JSON-schema-constrained output shape — everything else (PDF-to-message,
/// structured-output call, response splitting, token accounting) lives here.
///
/// <para>The Anthropic SDK is reached through Microsoft.Extensions.AI's
/// <see cref="IChatClient"/>: <c>DataContent</c> with the <c>application/pdf</c>
/// media type maps to Anthropic's document block; <c>ChatOptions.ResponseFormat</c>
/// = <c>ChatResponseFormat.ForJsonSchema</c> forces structured output.</para>
/// </summary>
internal abstract class SonnetExtractorBase : IDocTypeExtractor
{
    /// <summary>
    /// Model id landing on <c>document_extractions.model</c>. Part of the
    /// idempotency key — bumping it invalidates the cache (spec §"Why the
    /// unique-by-(model, prompt_hash)").
    /// </summary>
    public const string ModelId = "claude-sonnet-4-6";

    // Temperature = 0 for deterministic extraction. The LLM still varies under
    // load (provider-side caching, batching), but t=0 is the only honest knob.
    private const float Temperature = 0f;

    // Conservative ceiling on the LLM output: 6-field extractions land
    // ~400 tokens; 2 KB output covers the full quartet of doc types with
    // headroom for verbose status strings. Cuts off runaway completions.
    private const int MaxOutputTokens = 2048;

    private readonly IChatClient _chat;
    private readonly IPromptLoader _prompts;
    private readonly PromptHasher _hasher;
    private readonly ILogger _logger;

    public abstract DocType DocType { get; }
    public abstract string SchemaVersion { get; }
    public abstract string PromptResourceName { get; }

    /// <summary>JSON-schema text the LLM response must conform to.</summary>
    protected abstract string JsonSchema { get; }

    /// <summary>
    /// Human-friendly schema name passed to the Anthropic adapter. Used as a
    /// tool-name hint when MS.Extensions.AI synthesizes a forced tool call to
    /// enforce structured output.
    /// </summary>
    protected abstract string SchemaName { get; }

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
    }

    public async Task<ExtractionResult> ExtractAsync(ReadOnlyMemory<byte> pdf, CancellationToken ct)
    {
        if (pdf.IsEmpty)
            throw new ArgumentException("PDF bytes were empty.", nameof(pdf));

        var systemPrompt = await _prompts.LoadAsync(PromptResourceName, ct);
        var promptHash = await _hasher.HashOfAsync(PromptResourceName, ct);

        // System message: the loaded prompt verbatim.
        // User message: the PDF bytes as a document content block + a thin
        // instruction. Sonnet reads the PDF; the schema enforces the shape.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, new List<AIContent>
            {
                new DataContent(pdf, "application/pdf"),
                new TextContent("Extract the fields per the system instructions and the response schema."),
            }),
        };

        using var schemaDoc = JsonDocument.Parse(JsonSchema);
        var options = new ChatOptions
        {
            ModelId = ModelId,
            Temperature = Temperature,
            MaxOutputTokens = MaxOutputTokens,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                schemaDoc.RootElement,
                schemaName: SchemaName,
                schemaDescription: $"Structured output for {DocType} extraction ({SchemaVersion})."),
        };

        var response = await _chat.GetResponseAsync(messages, options, ct);

        var rawJson = ExtractStructuredJson(response);
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new ExtractorResponseException(
                $"Extractor {DocType} returned no usable JSON in response (neither Text nor FunctionCallContent).");

        var (fieldsJson, locationsJson, confidenceJson) = SplitLlmResponse(rawJson);

        // Token counts are best-effort: M.E.AI populates UsageDetails when the
        // provider returns it. Anthropic returns it on the final message; we
        // clamp to 0 if missing rather than throw — input tokens being absent
        // shouldn't fail the call, just lose the cost-accounting fidelity.
        var inputTokens = (int)(response.Usage?.InputTokenCount ?? 0);
        var outputTokens = (int)(response.Usage?.OutputTokenCount ?? 0);

        if (outputTokens >= (int)(MaxOutputTokens * 0.9))
        {
            _logger.LogWarning(
                "Sonnet extraction near MaxOutputTokens ceiling: docType={DocType}, out={OutTokens}/{Cap} — risk of truncated JSON",
                DocType, outputTokens, MaxOutputTokens);
        }

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
    /// Pulls the structured JSON out of a M.E.AI <see cref="ChatResponse"/>. The
    /// Anthropic adapter for <c>ChatResponseFormat.ForJsonSchema</c> synthesizes
    /// a forced tool call; depending on adapter version the JSON surfaces either
    /// as the assistant text (<c>response.Text</c>) or as the <c>Arguments</c>
    /// of a <see cref="FunctionCallContent"/> on the message contents. We try
    /// both so a minor M.E.AI bump doesn't silently start returning empty.
    /// </summary>
    internal static string ExtractStructuredJson(ChatResponse response)
    {
        var text = response.Text;
        if (!string.IsNullOrWhiteSpace(text))
            return text;

        foreach (var msg in response.Messages)
        {
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fc && fc.Arguments is { Count: > 0 } args)
                    return JsonSerializer.Serialize(args);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Splits Sonnet's combined response into the three storage-shape JSONBs.
    /// Sonnet returns <c>{ fields: { k: { value, page, bbox } }, confidence: { k: x } }</c>;
    /// the DB has separate columns for values, locations, and confidences.
    /// </summary>
    internal static (string Fields, string Locations, string Confidence) SplitLlmResponse(string rawJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            throw new ExtractorResponseException(
                $"Extractor response was not valid JSON: {Truncate(rawJson)}", ex);
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

            // Build the value-only map + the location map by walking the fields
            // object. Each per-field envelope is { value, page, bbox } — we
            // pluck `value` into one map, `(page, bbox)` into another. Missing
            // sub-properties on any field is a hard failure: the schema-forced
            // output should never omit them, so reaching here without them is
            // an LLM contract break, not a recoverable input.
            return (
                WriteToString(w =>
                {
                    w.WriteStartObject();
                    foreach (var prop in fields.EnumerateObject())
                    {
                        if (!prop.Value.TryGetProperty("value", out var v))
                            throw new ExtractorResponseException(
                                $"Field '{prop.Name}' missing 'value' in extractor response.");
                        w.WritePropertyName(prop.Name);
                        v.WriteTo(w);
                    }
                    w.WriteEndObject();
                }),
                WriteToString(w =>
                {
                    w.WriteStartObject();
                    foreach (var prop in fields.EnumerateObject())
                    {
                        if (!prop.Value.TryGetProperty("page", out var pageProp))
                            throw new ExtractorResponseException(
                                $"Field '{prop.Name}' missing 'page' in extractor response.");
                        if (!prop.Value.TryGetProperty("bbox", out var bboxProp))
                            throw new ExtractorResponseException(
                                $"Field '{prop.Name}' missing 'bbox' in extractor response.");

                        w.WritePropertyName(prop.Name);
                        w.WriteStartObject();
                        w.WritePropertyName("page");
                        pageProp.WriteTo(w);
                        w.WritePropertyName("bbox");
                        bboxProp.WriteTo(w);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }),
                confidence.GetRawText()
            );
        }
    }

    private static string WriteToString(Action<Utf8JsonWriter> body)
    {
        // ArrayBufferWriter + GetSpan avoids the MemoryStream.ToArray() copy
        // (writer.WrittenSpan is the already-flushed buffer).
        var buf = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf))
        {
            body(w);
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static string Truncate(string s) =>
        s.Length <= 200 ? s : s[..200] + "…";
}

public sealed class ExtractorResponseException : Exception
{
    public ExtractorResponseException(string message) : base(message) { }
    public ExtractorResponseException(string message, Exception inner) : base(message, inner) { }
}
