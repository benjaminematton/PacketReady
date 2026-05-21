using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Validators;

/// <summary>
/// Checks the provider's state medical license:
/// <list type="bullet">
///   <item>Critical — no license on file.</item>
///   <item>Critical — license <see cref="LicenseStatus.Active">status</see> is not Active.</item>
///   <item>Critical — expiry date is strictly before today (industry convention:
///         "expires today" is still valid; expired means <c>expiry &lt; today</c>).</item>
///   <item>Major — license is in a state other than the credentialing state.</item>
///   <item>Minor — license is still valid but expires within 30 days (renewal window).</item>
/// </list>
///
/// <para>Multi-emit: an expired-and-wrong-state license produces two Issues
/// (Critical + Major). The renewal-window Minor is suppressed when the license is
/// already expired — pointless to nag about a 12-day window if we've flagged it expired.</para>
/// </summary>
public sealed class LicenseStatusValidator(TimeProvider clock) : IValidator
{
    public string Name => "license_status";

    public Task<IReadOnlyList<Issue>> RunAsync(ProviderProfile profile, CancellationToken ct)
    {
        var issues = new List<Issue>();
        var today = clock.Today();

        if (profile.License is null)
        {
            issues.Add(new Issue(
                Validator: Name,
                Severity: Severity.Critical,
                Message: "No license on file — required for credentialing.",
                Remediation: "Provider must upload an active state medical license.",
                Citations: Array.Empty<Citation>()));
            return Task.FromResult<IReadOnlyList<Issue>>(issues);
        }

        var lic = profile.License;
        IReadOnlyList<Citation> cite =
            [new Citation(Name, $"{lic.State} {lic.Number} status={lic.Status} expires={lic.ExpiryDate:yyyy-MM-dd}")];

        if (lic.Status != LicenseStatus.Active)
            issues.Add(new Issue(Name, Severity.Critical,
                $"License status is {lic.Status}; must be Active.",
                "Resolve license status with the issuing board.", cite));

        // Industry convention: valid through the expiry date inclusive. "Expires today"
        // is still valid; "expired" means expiry_date < today.
        if (lic.ExpiryDate < today)
            issues.Add(new Issue(Name, Severity.Critical,
                $"License expired on {lic.ExpiryDate:yyyy-MM-dd}.",
                "Renew with the state board before submission.", cite));

        if (lic.State != profile.CredentialingState)
            issues.Add(new Issue(Name, Severity.Major,
                $"License is in {lic.State}; credentialing for {profile.CredentialingState}.",
                "Confirm the provider is licensed in the credentialing state, or update the target.",
                cite));

        // Only emit renewal-window Minor when the license is otherwise valid.
        if (lic.ExpiryDate >= today
            && (lic.ExpiryDate.DayNumber - today.DayNumber) < 30)
            issues.Add(new Issue(Name, Severity.Minor,
                $"License expires in {lic.ExpiryDate.DayNumber - today.DayNumber} days.",
                "Renewal recommended before payer submission.", cite));

        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }
}
