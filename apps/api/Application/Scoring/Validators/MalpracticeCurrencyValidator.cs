using PacketReady.Application.Payers;
using PacketReady.Application.Providers.Aggregation;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Validators;

/// <summary>
/// Owns the full malpractice surface (P4 — design.md §7.6 promised this in P1
/// but the validator never shipped). Four checks, all reading
/// <see cref="ProviderProfile.Malpractice"/>:
/// <list type="bullet">
///   <item>Critical — <see cref="MalpracticeInfo.Status"/> is not Active.</item>
///   <item>Critical — expiry strictly before today.</item>
///   <item>Major — coverage limits below the payer's required minimum.
///         Emitted only when the extracted value is non-null and below;
///         null means "extractor couldn't read it" and that's the
///         aggregator's Partial-Extraction lane, not ours.</item>
///   <item>Minor — still valid AND active but expires within the payer's
///         renewal window (default 30 days for payer-a, 60 days for payer-b).
///         The Status==Active gate avoids stacking a renewal-soon Minor on
///         top of an already-emitted status Critical for a Lapsed/Cancelled
///         policy whose printed ExpiryDate happens to still be in the future.</item>
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
    private readonly IPayerCatalog _payers;

    public MalpracticeCurrencyValidator(
        TimeProvider clock,
        IPayerCatalog payers)
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

        // Payer resolution is fail-loud: PayerNotConfiguredException maps to
        // a 422 at the API boundary. In practice the score handler validates
        // payerId at entry, so this branch only fires if the catalog itself
        // is shadow-mutated mid-run — defensive-but-cheap.
        var payer = _payers.Get(payerId);

        var issues = new List<Issue>();
        var today = _clock.Today();
        var mp = profile.Malpractice;

        // Two parallel citations so the dashboard's drill-in lands on the
        // status field for status Issues and the expiry field for expiry
        // Issues. Building one shared `expiryCite` and reusing it for the
        // status Critical sends the operator to the wrong PDF region.
        IReadOnlyList<Citation> statusCite = [provenance.Cite(
            Name,
            $"{mp.Carrier} {mp.PolicyNumber} status={mp.Status}",
            "malpractice.status")];
        IReadOnlyList<Citation> expiryCite = [provenance.Cite(
            Name,
            $"{mp.Carrier} {mp.PolicyNumber} expires={mp.ExpiryDate:yyyy-MM-dd}",
            "malpractice.expiryDate")];

        if (mp.Status != MalpracticeStatus.Active)
            issues.Add(new Issue(Name, Severity.Critical,
                $"Malpractice status is {mp.Status}; must be Active.",
                "Confirm an active malpractice policy with the carrier before submission.",
                statusCite));

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

        // Renewal-window Minor only fires when the policy is otherwise valid:
        // Active status AND expiry not yet past. Without the Active gate, a
        // Lapsed/Cancelled policy whose printed ExpiryDate is still in the
        // future would emit both Critical (status) and Minor (renewal-soon)
        // for the same already-unsubmittable packet — pure noise on the
        // dashboard. The window comes from payer YAML
        // (payer-a: 30 days, payer-b: 60 days).
        if (mp.Status == MalpracticeStatus.Active
            && mp.ExpiryDate >= today
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
