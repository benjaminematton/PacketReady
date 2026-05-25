using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Extraction.Classify;
using PacketReady.Application.Prompts;
using PacketReady.Domain.Documents;

namespace PacketReady.Infrastructure.Extraction.Classifier;

/// <summary>
/// Haiku-backed <see cref="IDocumentClassifier"/>. Reads the embedded
/// classifier prompt + the PDF as a vision input, calls
/// <c>claude-haiku-4-5</c> with a forced structured output of
/// <c>{ docType, confidence, rationale }</c>, and maps the camelCase
/// <c>docType</c> back to the <see cref="DocType"/> enum.
///
/// <para>The schema is the same Anthropic-friendly subset the Sonnet
/// extractors use: <c>type</c>, <c>properties</c>, <c>required</c>,
/// <c>additionalProperties</c>, <c>enum</c>. No <c>minimum</c>/<c>maximum</c>
/// on confidence — the API server rejects them; the prompt enforces the
/// [0, 1] range.</para>
/// </summary>
internal sealed class HaikuDocumentClassifier : IDocumentClassifier
{
    public const string ModelId = "claude-haiku-4-5";

    // Tiny output — three fields, ~80 tokens. The cap is generous to allow a
    // rationale sentence without truncation; runaway completions are unlikely.
    private const int MaxOutputTokens = 256;
    private const float Temperature = 0f;

    // Maps the wire-format string (lowercase camelCase per spec) to the
    // PascalCase domain enum. Throws on unmapped — if the model returns a
    // value outside the locked label set, that's a contract break, not
    // recoverable input.
    private static readonly IReadOnlyDictionary<string, DocType> DocTypeWireMap =
        new Dictionary<string, DocType>(StringComparer.Ordinal)
        {
            ["license"]     = DocType.License,
            ["dea"]         = DocType.Dea,
            ["boardCert"]   = DocType.BoardCert,
            ["malpractice"] = DocType.Malpractice,
            ["cv"]          = DocType.Cv,
            ["other"]       = DocType.Other,
        };

    private const string SchemaName = "document_classification";

    // Locked schema — matches the prompt's "Output" section verbatim. Anthropic
    // structured-output requires concrete types; the supported keyword subset
    // is `type`, `properties`, `required`, `additionalProperties`, `enum`.
    private const string SchemaJson = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["docType", "confidence", "rationale"],
      "properties": {
        "docType":    { "type": "string", "enum": ["license", "dea", "boardCert", "malpractice", "cv", "other"] },
        "confidence": { "type": "number" },
        "rationale":  { "type": "string" }
      }
    }
    """;

    // Singleton lifetime → parse the schema once at type load. Lazy<> would be
    // pure noise on a never-recycled instance field.
    private static readonly JsonDocument SchemaDoc = JsonDocument.Parse(SchemaJson);

    private readonly IChatClient _chat;
    private readonly IPromptLoader _prompts;
    private readonly PromptHasher _hasher;
    private readonly ILogger<HaikuDocumentClassifier> _logger;

    public HaikuDocumentClassifier(
        IChatClient chat,
        IPromptLoader prompts,
        PromptHasher hasher,
        ILogger<HaikuDocumentClassifier> logger)
    {
        _chat = chat;
        _prompts = prompts;
        _hasher = hasher;
        _logger = logger;
    }

    public async Task<ClassificationResult> ClassifyAsync(
        ReadOnlyMemory<byte> pdf,
        CancellationToken ct)
    {
        if (pdf.IsEmpty)
            throw new ArgumentException("PDF bytes were empty.", nameof(pdf));

        var systemPrompt = await _prompts.LoadAsync(PromptKeys.Classifier, ct);
        var promptHash = await _hasher.HashOfAsync(PromptKeys.Classifier, ct);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, new List<AIContent>
            {
                new DataContent(pdf, "application/pdf"),
                new TextContent("Classify this document per the system instructions and the response schema."),
            }),
        };

        var options = new ChatOptions
        {
            ModelId = ModelId,
            Temperature = Temperature,
            MaxOutputTokens = MaxOutputTokens,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                SchemaDoc.RootElement,
                schemaName: SchemaName,
                schemaDescription: "Single-label classification of an uploaded credentialing document."),
        };

        var response = await _chat.GetResponseAsync(messages, options, ct);

        var rawJson = ChatResponseParser.ExtractStructuredJson(response);
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new ClassifierResponseException(
                "Classifier returned no usable JSON (neither FunctionCallContent nor Text).");

        var (docType, confidence, rationale) = ParseResponse(rawJson, _logger);

        var inputTokens = (int)(response.Usage?.InputTokenCount ?? 0);
        var outputTokens = (int)(response.Usage?.OutputTokenCount ?? 0);

        _logger.LogInformation(
            "Haiku classification: docType={DocType}, confidence={Conf:F2}, in={InTokens}, out={OutTokens}",
            docType, confidence, inputTokens, outputTokens);

        return new ClassificationResult(
            DocType: docType,
            Confidence: confidence,
            Rationale: rationale,
            Model: ModelId,
            PromptHash: promptHash,
            InputTokens: inputTokens,
            OutputTokens: outputTokens);
    }

    // Pulled out as internal-static so handler tests can fixture-drive it
    // without spinning up an IChatClient mock. The logger is optional so tests
    // can pass null; production callers always supply one.
    internal static (DocType DocType, double Confidence, string Rationale) ParseResponse(
        string rawJson,
        ILogger? logger = null)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            throw new ClassifierResponseException(
                $"Classifier response was not valid JSON: {ChatResponseParser.TruncateForError(rawJson)}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ClassifierResponseException(
                    $"Classifier response was not a JSON object (kind={root.ValueKind}).");

            if (!root.TryGetProperty("docType", out var docTypeEl) || docTypeEl.ValueKind != JsonValueKind.String)
                throw new ClassifierResponseException("Classifier response missing string 'docType'.");
            if (!root.TryGetProperty("confidence", out var confEl) || confEl.ValueKind != JsonValueKind.Number)
                throw new ClassifierResponseException("Classifier response missing numeric 'confidence'.");
            if (!root.TryGetProperty("rationale", out var ratEl) || ratEl.ValueKind != JsonValueKind.String)
                throw new ClassifierResponseException("Classifier response missing string 'rationale'.");

            var docTypeWire = docTypeEl.GetString()!;
            if (!DocTypeWireMap.TryGetValue(docTypeWire, out var docType))
                throw new ClassifierResponseException(
                    $"Classifier returned unmapped docType '{docTypeWire}'. " +
                    $"Expected one of: {string.Join(", ", DocTypeWireMap.Keys)}.");

            // Clamp to [0, 1]. Anthropic's structured-output subset can't
            // constrain numeric ranges; a model returning 1.5 is a prompt-
            // following bug, not a meaningful overclaim. Log when the clamp
            // fires so prompt drift is visible in telemetry instead of being
            // silently masked.
            var raw = confEl.GetDouble();
            var confidence = Math.Clamp(raw, 0.0, 1.0);
            if (confidence != raw)
                logger?.LogWarning(
                    "Classifier confidence {Raw} out of [0, 1]; clamped to {Clamped}. Investigate prompt drift.",
                    raw, confidence);

            return (docType, confidence, ratEl.GetString()!);
        }
    }
}

/// <summary>
/// Thrown when the classifier's structured response is missing, malformed, or
/// outside the locked label set. Mirrors <see cref="SonnetExtractors.ExtractorResponseException"/>
/// on the extractor side — both signal "the model misbehaved" and the API
/// layer maps them identically.
/// </summary>
public sealed class ClassifierResponseException : Exception
{
    public ClassifierResponseException(string message) : base(message) { }
    public ClassifierResponseException(string message, Exception inner) : base(message, inner) { }
}
