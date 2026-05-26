using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Llm;
using PacketReady.Application.Nucc;
using PacketReady.Application.Prompts;
using PacketReady.Application.Providers.Aggregation;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Validators;

/// <summary>
/// P4 LLM validator #2. Two-step check that the NUCC taxonomy code printed
/// on the license refers to the same clinical specialty the board cert
/// states.
///
/// <list type="number">
///   <item><b>Deterministic CSV lookup.</b> <see cref="INuccTaxonomyLookup"/>
///         maps the taxonomy code to its NUCC Display Name. O(1), no LLM.</item>
///   <item><b>Thin LLM compare.</b> Send only
///         <c>{ canonicalSpecialty, statedSpecialty }</c> (~50 input tokens)
///         to Sonnet and ask "does the stated specialty semantically match
///         the canonical one?" Structured output:
///         <c>{ matches: bool, suggestedFix: string | null }</c>.</item>
/// </list>
///
/// <para>Sending the full ~900-row NUCC table to Sonnet on every call would
/// burn ~30k input tokens per validator run for no benefit. We don't.</para>
///
/// <para><b>Short-circuit paths</b> (no LLM call):
/// <list type="bullet">
///   <item>License sub-record null → aggregator owns Missing-License lane.</item>
///   <item>BoardCert sub-record null → aggregator owns Missing-BoardCert lane
///         (or BoardCertificationValidator's payer-config branch silences it).</item>
///   <item>License has no taxonomy code printed → no signal to compare against.</item>
///   <item>NUCC lookup misses → the taxonomy code is malformed or from a
///         newer NUCC revision than the snapshot; "no signal" is safer than
///         a fabricated Critical.</item>
/// </list>
/// </para>
///
/// <para><b>FP discipline (P4 task 10 tuning):</b> the gate is FP &lt; 5%
/// on the 5 conflict-free packets in the 10-packet tuning subset. The
/// prompt is conservative — when in doubt, return <c>matches: true</c>
/// — because a wrongly-emitted Critical is worse than a missed mismatch.
/// Recall ≥ 80% is the secondary target.</para>
/// </summary>
public sealed class NpiTaxonomyMatchValidator : IValidator
{
    public string Name => "npi_taxonomy_match";

    /// <summary>Pinned model id — same rationale as IdentityCoherence.</summary>
    public const string ModelId = "claude-sonnet-4-6";

    private const float Temperature = 0f;
    private const int MaxOutputTokens = 256;
    private const string SchemaName = "npi_taxonomy_match";

    private readonly IChatClient _chat;
    private readonly IPromptLoader _prompts;
    private readonly INuccTaxonomyLookup _nucc;
    private readonly ILogger<NpiTaxonomyMatchValidator> _logger;

    private static readonly JsonElement SchemaRoot =
        JsonDocument.Parse(SchemaJson).RootElement;

    public NpiTaxonomyMatchValidator(
        IChatClient chat,
        IPromptLoader prompts,
        INuccTaxonomyLookup nucc,
        ILogger<NpiTaxonomyMatchValidator> logger)
    {
        _chat = chat;
        _prompts = prompts;
        _nucc = nucc;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Issue>> RunAsync(
        ProviderProfile profile,
        IReadOnlyDictionary<string, FieldProvenance> provenance,
        string payerId,
        CancellationToken ct)
    {
        if (profile.License is null || profile.BoardCert is null)
            return Array.Empty<Issue>();

        var taxonomyCode = profile.License.TaxonomyCode;
        var statedSpecialty = profile.BoardCert.Specialty;
        if (string.IsNullOrWhiteSpace(taxonomyCode)
            || string.IsNullOrWhiteSpace(statedSpecialty))
        {
            return Array.Empty<Issue>();
        }

        if (!_nucc.TryGet(taxonomyCode, out var canonicalSpecialty))
        {
            _logger.LogInformation(
                "NpiTaxonomyMatch: taxonomy code {Code} not in NUCC snapshot — skipping.",
                taxonomyCode);
            return Array.Empty<Issue>();
        }

        var systemPrompt = await _prompts.LoadAsync(PromptKeys.NpiTaxonomyMatch, ct);
        var userPayload = JsonSerializer.Serialize(new
        {
            canonicalSpecialty,
            statedSpecialty,
        });

        var options = new ChatOptions
        {
            ModelId = ModelId,
            Temperature = Temperature,
            MaxOutputTokens = MaxOutputTokens,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                SchemaRoot,
                schemaName: SchemaName,
                schemaDescription: "Specialty-vs-taxonomy match decision."),
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPayload),
        };

        var response = await _chat.GetResponseAsync(messages, options, ct);
        var rawJson = ChatResponseParser.ExtractStructuredJson(response);

        TaxonomyMatchResponse parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TaxonomyMatchResponse>(rawJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? new TaxonomyMatchResponse(true, null);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "NpiTaxonomyMatch response was not valid JSON; treating as match: {Snippet}",
                ChatResponseParser.TruncateForError(rawJson));
            return Array.Empty<Issue>();
        }

        _logger.LogInformation(
            "NpiTaxonomyMatch: in={InTokens}, out={OutTokens}, matches={Matches}",
            response.Usage?.InputTokenCount ?? 0,
            response.Usage?.OutputTokenCount ?? 0,
            parsed.Matches);

        if (parsed.Matches)
            return Array.Empty<Issue>();

        // Mismatch → one Critical citing both source fields. The dashboard's
        // side-panel can render the suggested fix as the remediation hint.
        var suggested = string.IsNullOrWhiteSpace(parsed.SuggestedFix)
            ? canonicalSpecialty
            : parsed.SuggestedFix;

        var citations = new List<Citation>
        {
            provenance.Cite(Name, taxonomyCode, "license.taxonomyCode"),
            provenance.Cite(Name, statedSpecialty, "boardCert.specialty"),
        };

        return new[]
        {
            new Issue(
                Validator: Name,
                Severity: Severity.Critical,
                Message: $"License taxonomy code {taxonomyCode} maps to {canonicalSpecialty}, but board certification states {statedSpecialty}.",
                Remediation: $"Confirm the provider's specialty. Suggested: {suggested}.",
                Citations: citations)
            {
                // Field discriminator for the conflict-metrics runner. Pins
                // this Issue as the taxonomy_specialty_mismatch catch, not a
                // tangential identity-side finding.
                Field = "boardCert.specialty",
            },
        };
    }

    private sealed record TaxonomyMatchResponse(bool Matches, string? SuggestedFix);

    /// <summary>
    /// JSON schema for the forced-tool output. Same Anthropic subset as
    /// IdentityCoherence's schema; no banned keywords.
    /// </summary>
    private const string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "matches":      { "type": "boolean" },
            "suggestedFix": { "type": ["string", "null"] }
          },
          "required": ["matches", "suggestedFix"],
          "additionalProperties": false
        }
        """;
}
