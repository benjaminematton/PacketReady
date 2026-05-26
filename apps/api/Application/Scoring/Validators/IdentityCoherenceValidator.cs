using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Llm;
using PacketReady.Application.Prompts;
using PacketReady.Application.Providers.Aggregation;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Validators;

/// <summary>
/// Cross-document identity check (P4 LLM validator #1). Reads the
/// <c>fullName</c> field extracted from each present document and asks Sonnet
/// "do these all refer to the same person?", emitting one
/// <see cref="Issue"/> per disagreement.
///
/// <para><b>Scope is fullName for P4.</b> The design doc lists
/// <c>fullName</c>/<c>dateOfBirth</c>/<c>npi</c>/<c>address</c>, but only
/// <c>fullName</c> is extracted per-document today (the four extractors all
/// carry it; DOB/NPI/address sit on the aggregated profile or land in P4.5+).
/// Expanding the field set is a prompt change + a per-doc field add — not a
/// validator-shape change.</para>
///
/// <para><b>Per-doc data, not aggregated.</b> The validator reads
/// <see cref="LicenseInfo.FullName"/>, <see cref="DeaInfo.FullName"/>,
/// <see cref="BoardCertInfo.FullName"/>, and <see cref="MalpracticeInfo.FullName"/>
/// directly off the profile's sub-records (each one is what the extractor
/// pulled off that specific PDF). Null sub-records are skipped — the aggregator
/// owns the Missing-Document Critical for those, and double-emission would be
/// noise. Fewer than two present names → no possible conflict → no API call.</para>
///
/// <para><b>FP discipline.</b> P4 task 9's tuning gate is FP &lt; 5% on the
/// 30 conflict-free packets across the dataset. The prompt instructs Sonnet to
/// only flag a real disagreement — typo normalizations ("Henry Anderson" vs
/// "Henry Anderson, MD") don't count. Tighten the prompt, not the
/// post-processing, if FPs creep up.</para>
///
/// <para>Overlaps with the aggregator's existing Levenshtein-≥3 fullName Minor:
/// both can fire on the same disagreement (IdentityCoherence emits a Critical,
/// aggregator emits a Minor). Acceptable for P4 ship; deduplication is a P4.5
/// concern once the LLM validator's FP profile is settled.</para>
/// </summary>
public sealed class IdentityCoherenceValidator : IValidator
{
    public string Name => "identity_coherence";

    /// <summary>
    /// Pinned model id — bumping invalidates the tuning baseline. The
    /// IdentityCoherence FP/recall numbers in <c>baseline.json</c> are tied
    /// to a specific Sonnet revision; a silent model drift would move the
    /// numbers without a corresponding prompt change.
    /// </summary>
    public const string ModelId = "claude-sonnet-4-6";

    private const float Temperature = 0f;
    private const int MaxOutputTokens = 1024;
    private const string SchemaName = "identity_coherence";

    private readonly IChatClient _chat;
    private readonly IPromptLoader _prompts;
    private readonly ILogger<IdentityCoherenceValidator> _logger;

    // Parsed once at type init — the schema is constant and the JsonElement
    // keeps the underlying JsonDocument rooted for process lifetime.
    private static readonly JsonElement SchemaRoot =
        JsonDocument.Parse(SchemaJson).RootElement;

    public IdentityCoherenceValidator(
        IChatClient chat,
        IPromptLoader prompts,
        ILogger<IdentityCoherenceValidator> logger)
    {
        _chat = chat;
        _prompts = prompts;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Issue>> RunAsync(
        ProviderProfile profile,
        IReadOnlyDictionary<string, FieldProvenance> provenance,
        string payerId,
        CancellationToken ct)
    {
        var sources = CollectFullNameSources(profile);
        if (sources.Count < 2)
            return Array.Empty<Issue>();

        // Indexed lookup for citation construction. The LLM response only
        // echoes docType labels; the per-doc name strings stay on our side.
        var namesByDocType = sources.ToDictionary(
            s => s.DocType, s => s.FullName, StringComparer.Ordinal);

        var systemPrompt = await _prompts.LoadAsync(PromptKeys.IdentityCoherence, ct);

        var userPayload = JsonSerializer.Serialize(new
        {
            field = "fullName",
            sources = sources.Select(s => new { docType = s.DocType, extractedValue = s.FullName }),
        });

        var options = new ChatOptions
        {
            ModelId = ModelId,
            Temperature = Temperature,
            MaxOutputTokens = MaxOutputTokens,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                SchemaRoot,
                schemaName: SchemaName,
                schemaDescription: "Cross-document identity coherence findings."),
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPayload),
        };

        var response = await _chat.GetResponseAsync(messages, options, ct);
        var rawJson = ChatResponseParser.ExtractStructuredJson(response);

        IdentityCoherenceResponse parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<IdentityCoherenceResponse>(rawJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? new IdentityCoherenceResponse([]);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "IdentityCoherence response was not valid JSON; treating as no-conflict: {Snippet}",
                ChatResponseParser.TruncateForError(rawJson));
            return Array.Empty<Issue>();
        }

        _logger.LogInformation(
            "IdentityCoherence: in={InTokens}, out={OutTokens}, disagreements={Count}",
            response.Usage?.InputTokenCount ?? 0,
            response.Usage?.OutputTokenCount ?? 0,
            parsed.Disagreements.Count);

        return parsed.Disagreements
            .Select(d => ToIssue(d, provenance, namesByDocType))
            .ToList();
    }

    private Issue ToIssue(
        IdentityDisagreement d,
        IReadOnlyDictionary<string, FieldProvenance> provenance,
        IReadOnlyDictionary<string, string> namesByDocType)
    {
        // Translate per-source docType strings into citations against the
        // per-doc fullName provenance entries. ExtractedValue is the actual
        // name string the extractor pulled off that doc — the dashboard chip
        // needs to render "Henry Anderson, MD", not the bucket label. Cite()
        // fills doc-ref nulls when the lookup misses, which keeps the Issue
        // shape stable even if a future extractor drops the fullName slot.
        var citations = d.Sources
            .Select(docType => provenance.Cite(
                sourceValidator: Name,
                extractedValue: namesByDocType.TryGetValue(docType, out var name) ? name : docType,
                provenanceKey: $"{docType}.fullName"))
            .ToList();

        // Field discriminator for the conflict-metrics runner. The planter
        // names the variant source (e.g. "malpractice.fullName"); the baseline
        // is always license. Picking the first non-license source verbatim
        // (the prior shape) was fragile — if Sonnet emits a 3-element
        // sources array like ["license", "dea", "malpractice"], we'd stamp
        // "dea.fullName" against a planted "malpractice.fullName" and silently
        // miss the catch. Instead: pick the first source whose extracted name
        // ACTUALLY differs from the license anchor. That isolates the real
        // outlier regardless of how many docs the LLM listed; degrades
        // gracefully (and deterministically) when the disagreement set is
        // ambiguous.
        var variantSource = PickVariantSource(d.Sources, namesByDocType);
        return new Issue(
            Validator: Name,
            Severity: ParseSeverity(d.Severity),
            Message: d.Message,
            Remediation: d.Remediation,
            Citations: citations)
        {
            Field = IssueFieldSpec.Format(variantSource, d.Field),
        };
    }

    /// <summary>
    /// Deterministic outlier selection across the disagreement's sources.
    /// Preference order:
    /// <list type="number">
    ///   <item>First non-license source whose extracted name differs from the
    ///         license anchor.</item>
    ///   <item>First non-license source (when license isn't in the set or
    ///         name comparisons are inconclusive).</item>
    ///   <item>First source (fully degenerate fallback — keeps the Field
    ///         discriminator populated even if the LLM returns only
    ///         <c>["license"]</c>).</item>
    /// </list>
    /// </summary>
    private static string PickVariantSource(
        IReadOnlyList<string> sources,
        IReadOnlyDictionary<string, string> namesByDocType)
    {
        if (sources.Count == 0) return "";

        namesByDocType.TryGetValue("license", out var licenseName);
        if (!string.IsNullOrEmpty(licenseName))
        {
            foreach (var s in sources)
            {
                if (s == "license") continue;
                if (namesByDocType.TryGetValue(s, out var name)
                    && !string.IsNullOrEmpty(name)
                    && !string.Equals(name, licenseName, StringComparison.Ordinal))
                {
                    return s;
                }
            }
        }

        var firstNonLicense = sources.FirstOrDefault(s => s != "license");
        return firstNonLicense ?? sources[0];
    }

    private static Severity ParseSeverity(string s) => s switch
    {
        "Critical" => Severity.Critical,
        "Minor"    => Severity.Minor,
        // Schema enum is locked to {Critical, Minor}; anything else is the LLM
        // going off-schema. Downgrade to Minor so a wandering response doesn't
        // fabricate a Critical the regression gate would then chase.
        _          => Severity.Minor,
    };

    private static List<NameSource> CollectFullNameSources(ProviderProfile profile)
    {
        var sources = new List<NameSource>(4);
        if (profile.License is { FullName: { Length: > 0 } ln })     sources.Add(new("license", ln));
        if (profile.Dea is { FullName: { Length: > 0 } dn })         sources.Add(new("dea", dn));
        if (profile.BoardCert is { FullName: { Length: > 0 } bn })   sources.Add(new("boardCert", bn));
        if (profile.Malpractice is { FullName: { Length: > 0 } mn }) sources.Add(new("malpractice", mn));
        return sources;
    }

    private sealed record NameSource(string DocType, string FullName);

    private sealed record IdentityCoherenceResponse(
        IReadOnlyList<IdentityDisagreement> Disagreements);

    private sealed record IdentityDisagreement(
        string Field,
        string Severity,
        string Message,
        string Remediation,
        IReadOnlyList<string> Sources);

    /// <summary>
    /// JSON schema for Sonnet's forced-tool output. Anthropic accepts a subset
    /// of JSON Schema (type / properties / required / additionalProperties /
    /// items / enum); we stick to that. Severity is an enum so the model
    /// can't invent "Severe" or "Warning".
    /// </summary>
    private const string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "disagreements": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "field":       { "type": "string", "enum": ["fullName"] },
                  "severity":    { "type": "string", "enum": ["Critical", "Minor"] },
                  "message":     { "type": "string" },
                  "remediation": { "type": "string" },
                  "sources":     { "type": "array", "items": { "type": "string", "enum": ["license", "dea", "boardCert", "malpractice"] } }
                },
                "required": ["field", "severity", "message", "remediation", "sources"],
                "additionalProperties": false
              }
            }
          },
          "required": ["disagreements"],
          "additionalProperties": false
        }
        """;
}
