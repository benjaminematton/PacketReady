using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Validators;

/// <summary>
/// Checks the provider's DEA registration. DEA is federal, so there's no state-match
/// concern; otherwise mirrors the license validator's ladder.
/// <list type="bullet">
///   <item>Critical — no DEA on file (treated as required for now; revisit when we
///         segment providers who don't prescribe controlled substances).</item>
///   <item>Critical — status is not Active.</item>
///   <item>Critical — expiry strictly before today.</item>
///   <item>Minor — still valid but expires within 30 days.</item>
/// </list>
/// </summary>
public sealed class DeaStatusValidator(TimeProvider clock) : IValidator
{
    public string Name => "dea_status";

    public Task<IReadOnlyList<Issue>> RunAsync(ProviderProfile profile, CancellationToken ct)
    {
        var issues = new List<Issue>();
        var today = clock.Today();

        if (profile.Dea is null)
        {
            issues.Add(new Issue(
                Validator: Name,
                Severity: Severity.Critical,
                Message: "No DEA registration on file.",
                Remediation: "Provider must upload a current DEA registration certificate.",
                Citations: Array.Empty<Citation>()));
            return Task.FromResult<IReadOnlyList<Issue>>(issues);
        }

        var dea = profile.Dea;
        IReadOnlyList<Citation> cite =
            [new Citation(Name, $"{dea.Number} status={dea.Status} expires={dea.ExpiryDate:yyyy-MM-dd}")];

        if (dea.Status != DeaStatus.Active)
            issues.Add(new Issue(Name, Severity.Critical,
                $"DEA status is {dea.Status}; must be Active.",
                "Resolve DEA status with the DEA registration office.", cite));

        if (dea.ExpiryDate < today)
            issues.Add(new Issue(Name, Severity.Critical,
                $"DEA expired on {dea.ExpiryDate:yyyy-MM-dd}.",
                "Renew DEA registration before submission.", cite));

        if (dea.ExpiryDate >= today
            && (dea.ExpiryDate.DayNumber - today.DayNumber) < 30)
            issues.Add(new Issue(Name, Severity.Minor,
                $"DEA expires in {dea.ExpiryDate.DayNumber - today.DayNumber} days.",
                "Renewal recommended before payer submission.", cite));

        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }
}
