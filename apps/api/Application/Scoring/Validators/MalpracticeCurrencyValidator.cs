using PacketReady.Application.Payers;
using PacketReady.Application.Providers.Aggregation;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Validators;

/// <summary>
/// Owns the full malpractice surface (P4 — design.md §7.6 promised this in P1
/// but the validator never shipped). Three checks, all reading
/// <see cref="ProviderProfile.Malpractice"/>:
/// <list type="bullet">
///   <item>Critical — <see cref="MalpracticeInfo.Status"/> is not Active.</item>
///   <item>Major — coverage limits below the payer's required minimum.
///         Emitted only when the extracted value is non-null and below;
///         null means "extractor couldn't read it" and that's the
///         aggregator's Partial-Extraction lane, not ours.</item>
///   <item>Critical — expiry strictly before today.</item>
///   <item>Minor — still valid but expires within the payer's renewal
///         window (default 30 days for payer-a, 60 days for payer-b).</item>
/// </list>
///
/// <para><b>Missing-malpractice</b> is owned by <c>IProviderProfileAggregator</c>
/// (same contract as the License/DEA/BoardCert validators); this validator
/// short-circuits when <see cref="ProviderProfile.Malpractice"/> is null to
/// avoid double-counting Criticals.</para>
///
/// <para>The two coverage checks emit independently — a policy that's $500k
/// per-occurrence but $5M aggregate against a $1M / $3M payer floor fires
/// one Major (per-occurrence below), not two.</para>
/// </summary>
public sealed class MalpracticeCurrencyValidator : IValidator
{
    public string Name => "malpractice_currency";

    private readonly TimeProvider _clock;
    private readonly IReadOnlyDictionary<string, PayerRequirement> _payers;

    public MalpracticeCurrencyValidator(
        TimeProvider clock,
        IReadOnlyDictionary<string, PayerRequirement> payers)
    {
        _clock = clock;
        _payers = payers;
    }

    public Task<IReadOnlyList<Issue>> RunAsync(
        ProviderProfile profile,
        IReadOnlyDictionary<string, FieldProvenance> provenance,
        string payerId,
        CancellationToken ct)
    {
        if (profile.Malpractice is null)
            return Task.FromResult<IReadOnlyList<Issue>>(Array.Empty<Issue>());

        // Payer resolution is fail-loud: an unknown id at run time means
        // either a schema-drifted DB row or a YAML that wasn't deployed.
        // Both are operator bugs; better a 500 with the missing id than
        // silently validating against payer-a's defaults.
        if (!_payers.TryGetValue(payerId, out var payer))
            throw new KeyNotFoundException(
                $"MalpracticeCurrencyValidator: payerId '{payerId}' is not backed by a YAML file. " +
                $"Known payers: [{string.Join(", ", _payers.Keys)}].");

        var issues = new List<Issue>();
        var today = _clock.Today();
        var mp = profile.Malpractice;
        IReadOnlyList<Citation> expiryCite = [provenance.Cite(
            Name,
            $"{mp.Carrier} {mp.PolicyNumber} status={mp.Status} expires={mp.ExpiryDate:yyyy-MM-dd}",
            "malpractice.expiryDate")];

        if (mp.Status != MalpracticeStatus.Active)
            issues.Add(new Issue(Name, Severity.Critical,
                $"Malpractice status is {mp.Status}; must be Active.",
                "Confirm an active malpractice policy with the carrier before submission.",
                expiryCite));

        if (mp.ExpiryDate < today)
            issues.Add(new Issue(Name, Severity.Critical,
                $"Malpractice expired on {mp.ExpiryDate:yyyy-MM-dd}.",
                "Renew the malpractice policy or obtain a current certificate of coverage.",
                expiryCite));

        // Coverage minimums — Major, not Critical. Below-minimum is a payer
        // negotiation, not a hard block; many providers can credential with
        // a top-up rider before submission.
        if (mp.PerOccurrence is { } occ && occ < payer.Malpractice.MinimumPerOccurrence)
        {
            IReadOnlyList<Citation> cite = [provenance.Cite(
                Name,
                $"perOccurrence=${occ:N0}",
                "malpractice.perOccurrence")];
            issues.Add(new Issue(Name, Severity.Major,
                $"Malpractice per-occurrence coverage ${occ:N0} is below the ${payer.Malpractice.MinimumPerOccurrence:N0} minimum required by {payer.Name}.",
                "Increase per-occurrence limits with the carrier, or confirm a rider before submission.",
                cite));
        }

        if (mp.Aggregate is { } agg && agg < payer.Malpractice.MinimumAggregate)
        {
            IReadOnlyList<Citation> cite = [provenance.Cite(
                Name,
                $"aggregate=${agg:N0}",
                "malpractice.aggregate")];
            issues.Add(new Issue(Name, Severity.Major,
                $"Malpractice aggregate coverage ${agg:N0} is below the ${payer.Malpractice.MinimumAggregate:N0} minimum required by {payer.Name}.",
                "Increase aggregate limits with the carrier, or confirm a rider before submission.",
                cite));
        }

        // Renewal-window Minor only fires when the policy is otherwise valid —
        // same convention as License/DEA. The window comes from payer YAML
        // (payer-a: 30 days, payer-b: 60 days).
        if (mp.ExpiryDate >= today
            && (mp.ExpiryDate.DayNumber - today.DayNumber) < payer.WindowDays.MalpracticeRenewal)
        {
            issues.Add(new Issue(Name, Severity.Minor,
                $"Malpractice expires in {mp.ExpiryDate.DayNumber - today.DayNumber} days.",
                "Renewal recommended before payer submission.",
                expiryCite));
        }

        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }
}
