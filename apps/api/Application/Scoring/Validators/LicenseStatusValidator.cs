using PacketReady.Application.Providers.Aggregation;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Validators;

/// <summary>
/// Checks the provider's state medical license:
/// <list type="bullet">
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
///
/// <para><b>Missing-license</b> is owned by <c>IProviderProfileAggregator</c> —
/// it emits a Missing-Document / Extraction-Failed / Partial-Extraction Critical
/// when <see cref="ProviderProfile.License"/> is null. This validator silently
/// short-circuits in that case to avoid double-counting Criticals.</para>
/// </summary>
public sealed class LicenseStatusValidator(TimeProvider clock) : IValidator
{
    public string Name => "license_status";

    public Task<IReadOnlyList<Issue>> RunAsync(
        ProviderProfile profile,
        IReadOnlyDictionary<string, FieldProvenance> provenance,
        string payerId,
        CancellationToken ct)
    {
        if (profile.License is null)
            return Task.FromResult<IReadOnlyList<Issue>>(Array.Empty<Issue>());

        var issues = new List<Issue>();
        var today = clock.Today();
        var lic = profile.License;

        // Parallel citations so the PDF drill-in lands on the field the
        // Issue is actually about — status Issue → status box, expiry
        // Issue → expiry box, state-mismatch → state box. A single shared
        // expiry citation (the prior shape) sent the operator to the wrong
        // region for status/state findings.
        IReadOnlyList<Citation> statusCite = [provenance.Cite(
            Name,
            $"{lic.State} {lic.Number} status={lic.Status}",
            "license.status")];
        IReadOnlyList<Citation> expiryCite = [provenance.Cite(
            Name,
            $"{lic.State} {lic.Number} expires={lic.ExpiryDate:yyyy-MM-dd}",
            "license.expiryDate")];
        IReadOnlyList<Citation> stateCite = [provenance.Cite(
            Name,
            $"{lic.State} {lic.Number}",
            "license.state")];

        if (lic.Status != LicenseStatus.Active)
            issues.Add(new Issue(Name, Severity.Critical,
                $"License status is {lic.Status}; must be Active.",
                "Resolve license status with the issuing board.", statusCite));

        // Industry convention: valid through the expiry date inclusive. "Expires today"
        // is still valid; "expired" means expiry_date < today.
        if (lic.ExpiryDate < today)
            issues.Add(new Issue(Name, Severity.Critical,
                $"License expired on {lic.ExpiryDate:yyyy-MM-dd}.",
                "Renew with the state board before submission.", expiryCite));

        if (lic.State != profile.CredentialingState)
            issues.Add(new Issue(Name, Severity.Major,
                $"License is in {lic.State}; credentialing for {profile.CredentialingState}.",
                "Confirm the provider is licensed in the credentialing state, or update the target.",
                stateCite));

        // Renewal-window Minor only fires when the license is otherwise valid:
        // Active status AND expiry not yet past. Matches the discipline in
        // MalpracticeCurrencyValidator and BoardCertificationValidator.
        if (lic.Status == LicenseStatus.Active
            && lic.ExpiryDate >= today
            && (lic.ExpiryDate.DayNumber - today.DayNumber) < 30)
            issues.Add(new Issue(Name, Severity.Minor,
                $"License expires in {lic.ExpiryDate.DayNumber - today.DayNumber} days.",
                "Renewal recommended before payer submission.", expiryCite));

        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }
}
