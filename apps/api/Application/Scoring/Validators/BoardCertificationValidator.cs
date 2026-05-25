using PacketReady.Application.Providers.Aggregation;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Validators;

/// <summary>
/// Checks board certification status. In P1 we only check presence/status/expiry —
/// the specialty-vs-NPI-taxonomy cross-check is the LLM-augmented
/// <c>npi_taxonomy_match</c> validator in Phase 4.
/// <list type="bullet">
///   <item>Critical — status is not Active.</item>
///   <item>Critical — expiry strictly before today.</item>
///   <item>Minor — still valid but expires within 30 days.</item>
/// </list>
///
/// <para>Missing-board-cert is owned by the aggregator (see <c>LicenseStatusValidator</c>);
/// this validator short-circuits when <see cref="ProviderProfile.BoardCert"/> is null.</para>
/// </summary>
public sealed class BoardCertificationValidator(TimeProvider clock) : IValidator
{
    public string Name => "board_certification";

    public Task<IReadOnlyList<Issue>> RunAsync(
        ProviderProfile profile,
        IReadOnlyDictionary<string, FieldProvenance> provenance,
        CancellationToken ct)
    {
        if (profile.BoardCert is null)
            return Task.FromResult<IReadOnlyList<Issue>>(Array.Empty<Issue>());

        var issues = new List<Issue>();
        var today = clock.Today();
        var bc = profile.BoardCert;
        IReadOnlyList<Citation> cite = [provenance.Cite(
            Name,
            $"{bc.Board} {bc.Specialty} status={bc.Status} expires={bc.ExpiryDate:yyyy-MM-dd}",
            "boardCert.expiryDate")];

        if (bc.Status != BoardCertStatus.Active)
            issues.Add(new Issue(Name, Severity.Critical,
                $"Board cert status is {bc.Status}; must be Active.",
                "Confirm current certification with the issuing board.", cite));

        if (bc.ExpiryDate < today)
            issues.Add(new Issue(Name, Severity.Critical,
                $"Board cert expired on {bc.ExpiryDate:yyyy-MM-dd}.",
                "Renew or recertify with the issuing board before submission.", cite));

        if (bc.ExpiryDate >= today
            && (bc.ExpiryDate.DayNumber - today.DayNumber) < 30)
            issues.Add(new Issue(Name, Severity.Minor,
                $"Board cert expires in {bc.ExpiryDate.DayNumber - today.DayNumber} days.",
                "Recertification recommended before payer submission.", cite));

        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }
}
