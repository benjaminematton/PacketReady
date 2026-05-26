using PacketReady.Application.Payers;
using PacketReady.Application.Providers.Aggregation;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Validators;

/// <summary>
/// Owns Missing-Document Critical for payer-required doc types <b>not</b> in
/// the universal-4 (license, dea, boardCert, malpractice). The aggregator
/// owns the universal-4 lane — see
/// <see cref="AggregatedProfile"/>'s Missing-Document bullet. This validator
/// must skip those types in code, not by coincidence, so the two sources
/// can't double-count.
///
/// <para><b>Scope in P4:</b> both committed payer YAMLs require only the
/// universal-4, so this validator emits nothing in the common case. It is
/// the forward-compatibility lane for payer #3+ — when a payer YAML names
/// a non-universal type (e.g. a future <c>stateRegistration</c> or
/// <c>cds</c>), the validator emits one Critical per missing one.</para>
///
/// <para><b>Doc-presence telemetry:</b> for P4 we don't yet plumb a
/// "what doc types has the provider uploaded?" surface into the validator
/// (the validator stays pure-code; doc-store access would require an
/// aggregator-driven extension to <see cref="ProviderProfile"/>). The
/// current implementation conservatively emits Critical for any non-universal
/// type listed by the payer — there's no signal yet that the provider
/// uploaded such a doc, and a payer requirement we can't verify is
/// safer-as-Critical than safer-as-Pass. When a payer first names a
/// non-universal type, the follow-on is to thread the uploaded-doc-types
/// list onto <see cref="AggregatedProfile"/> and consult it here. Until
/// then the validator is dormant scaffolding.</para>
///
/// <para>Citations are doc-less (<c>DocumentId</c>/<c>Page</c>/<c>Bbox</c>
/// all null) — there's nothing to point at when the doc isn't there. The
/// dashboard's IssueCard renders these as a "no source" pill.</para>
/// </summary>
public sealed class RequiredDocumentsValidator : IValidator
{
    public string Name => "required_documents";

    /// <summary>
    /// The four doc types <c>IProviderProfileAggregator</c> emits
    /// Missing-Document Critical for. Skip in code, not by coincidence —
    /// the layering split is locked. Strings are the camelCase docType
    /// labels that match payer YAML's <c>requiredDocuments</c> entries.
    /// </summary>
    public static readonly IReadOnlySet<string> UniversalDocTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "license", "dea", "boardCert", "malpractice",
        };

    private readonly IReadOnlyDictionary<string, PayerRequirement> _payers;

    public RequiredDocumentsValidator(
        IReadOnlyDictionary<string, PayerRequirement> payers)
    {
        _payers = payers;
    }

    public Task<IReadOnlyList<Issue>> RunAsync(
        ProviderProfile profile,
        IReadOnlyDictionary<string, FieldProvenance> provenance,
        string payerId,
        CancellationToken ct)
    {
        if (!_payers.TryGetValue(payerId, out var payer))
            throw new KeyNotFoundException(
                $"RequiredDocumentsValidator: payerId '{payerId}' is not backed by a YAML file. " +
                $"Known payers: [{string.Join(", ", _payers.Keys)}].");

        // Universal-4 entries are the aggregator's lane; everything else is
        // ours. Ordinal compare matches PayerRequirement loader's invariant.
        var nonUniversalRequired = payer.RequiredDocuments
            .Where(t => !UniversalDocTypes.Contains(t))
            .ToList();

        if (nonUniversalRequired.Count == 0)
            return Task.FromResult<IReadOnlyList<Issue>>(Array.Empty<Issue>());

        var issues = new List<Issue>(nonUniversalRequired.Count);
        foreach (var docType in nonUniversalRequired)
        {
            issues.Add(new Issue(
                Validator: Name,
                Severity: Severity.Critical,
                Message: $"{payer.Name} requires a {docType} document; none is on file.",
                Remediation: $"Upload a {docType} PDF via POST /api/providers/{{id}}/documents.",
                Citations: Array.Empty<Citation>()));
        }
        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }
}
