using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PacketReady.Domain.Providers;

/// <summary>
/// Aggregated, structured view of a provider's credentialing data. In Phase 1 this
/// is hand-curated and supplied as JSON. Phase 3 rebuilds it from extraction rows.
///
/// <para><b>Shape validation, not policy.</b> <see cref="Validate"/> enforces basic
/// shape invariants (NPI is 10 digits, state code is 2 letters, dates aren't from
/// the 19th century or 99 years in the future, issue ≤ expiry). Business rules —
/// "license is expired today", "DEA must cover schedule II" — are the validator
/// suite's job, not this method. Failing here throws and prevents bad data from
/// entering the system; failing in a validator surfaces a cited Issue in the score.</para>
///
/// <para><b>Three construction paths and their guarantees:</b>
/// <list type="bullet">
///   <item><see cref="Create"/> — validated against <c>nowUtc</c> before returning.
///         The only path callers in other assemblies can construct from raw fields.</item>
///   <item>STJ deserialization via the <see cref="JsonConstructorAttribute"/>-marked
///         private ctor — does <b>not</b> run <see cref="Validate"/>. Used by
///         <see cref="Provider.GetProfile"/>; the data was validated at write time
///         by <see cref="Provider.Create"/>, so re-validating on read would just
///         drift with the clock (a profile valid at write may fail today's ceiling).</item>
///   <item><c>with</c> expressions — bypass <see cref="Validate"/>. A mutated profile
///         that re-enters the system through <see cref="Provider.Create"/> is
///         re-validated there; one used in-memory only is the caller's problem.</item>
/// </list>
/// </para>
/// </summary>
public sealed record ProviderProfile
{
    public string FullName { get; init; }
    public DateOnly DateOfBirth { get; init; }
    public string Npi { get; init; }
    public string CredentialingState { get; init; }     // 2-letter state code, the state we're credentialing FOR
    public LicenseInfo? License { get; init; }
    public DeaInfo? Dea { get; init; }
    public BoardCertInfo? BoardCert { get; init; }
    public SanctionsResult? Sanctions { get; init; }

    private static readonly Regex NpiRegex = new(@"^\d{10}$", RegexOptions.Compiled);
    private static readonly Regex StateRegex = new(@"^[A-Z]{2}$", RegexOptions.Compiled);
    private static readonly DateOnly MinPlausibleDate = new(1900, 1, 1);

    /// <summary>
    /// How far in the future an expiry date may sit before we call it implausible.
    /// Credentials typically renew every 1–10 years; 50 is a generous ceiling that
    /// still catches typos like 2299.
    /// </summary>
    private const int MaxExpiryYearsAhead = 50;

    /// <summary>
    /// STJ deserialization entry point. Private so external callers must use
    /// <see cref="Create"/>. STJ binds by parameter name (case-insensitive) to
    /// the JSON property names. Validation is intentionally <b>not</b> called here —
    /// see the class doc-comment for why.
    /// </summary>
    [JsonConstructor]
    private ProviderProfile(
        string fullName,
        DateOnly dateOfBirth,
        string npi,
        string credentialingState,
        LicenseInfo? license,
        DeaInfo? dea,
        BoardCertInfo? boardCert,
        SanctionsResult? sanctions)
    {
        FullName = fullName;
        DateOfBirth = dateOfBirth;
        Npi = npi;
        CredentialingState = credentialingState;
        License = license;
        Dea = dea;
        BoardCert = boardCert;
        Sanctions = sanctions;
    }

    /// <summary>
    /// Only public construction path. Validates the resulting profile against
    /// <paramref name="nowUtc"/> before returning; throws <see cref="ArgumentException"/>
    /// on the first shape violation.
    /// </summary>
    public static ProviderProfile Create(
        string fullName,
        DateOnly dateOfBirth,
        string npi,
        string credentialingState,
        DateTimeOffset nowUtc,
        LicenseInfo? license = null,
        DeaInfo? dea = null,
        BoardCertInfo? boardCert = null,
        SanctionsResult? sanctions = null)
    {
        var profile = new ProviderProfile(
            fullName, dateOfBirth, npi, credentialingState,
            license, dea, boardCert, sanctions);
        Validate(profile, nowUtc);
        return profile;
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> on the first shape violation. Called
    /// from <see cref="Create"/> and again from <see cref="Provider.Create"/> as
    /// defense-in-depth for profiles that arrived via <c>with</c> or deserialization.
    /// </summary>
    public static void Validate(ProviderProfile profile, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var today = DateOnly.FromDateTime(nowUtc.UtcDateTime);
        var maxExpiry = today.AddYears(MaxExpiryYearsAhead);

        if (string.IsNullOrWhiteSpace(profile.FullName))
            throw new ArgumentException("FullName is required.", nameof(profile));

        if (!NpiRegex.IsMatch(profile.Npi))
            throw new ArgumentException(
                $"Npi must be exactly 10 digits; got '{profile.Npi}'.", nameof(profile));

        if (!StateRegex.IsMatch(profile.CredentialingState))
            throw new ArgumentException(
                $"CredentialingState must be 2 uppercase letters; got '{profile.CredentialingState}'.",
                nameof(profile));

        RequirePlausible(profile.DateOfBirth, "DateOfBirth", nameof(profile));
        if (profile.DateOfBirth > today)
            throw new ArgumentException(
                $"DateOfBirth in the future: {profile.DateOfBirth}.", nameof(profile));

        if (profile.License is { } lic)
        {
            if (string.IsNullOrWhiteSpace(lic.Number))
                throw new ArgumentException("License.Number is required.", nameof(profile));
            if (!StateRegex.IsMatch(lic.State))
                throw new ArgumentException($"License.State invalid: '{lic.State}'.", nameof(profile));
            RequireIssueDate(lic.IssueDate, today, "License.IssueDate", nameof(profile));
            RequireExpiryDate(lic.ExpiryDate, maxExpiry, "License.ExpiryDate", nameof(profile));
            if (lic.IssueDate > lic.ExpiryDate)
                throw new ArgumentException(
                    $"License.IssueDate ({lic.IssueDate}) after ExpiryDate ({lic.ExpiryDate}).",
                    nameof(profile));
        }

        if (profile.Dea is { } dea)
        {
            if (string.IsNullOrWhiteSpace(dea.Number))
                throw new ArgumentException("Dea.Number is required.", nameof(profile));
            RequireExpiryDate(dea.ExpiryDate, maxExpiry, "Dea.ExpiryDate", nameof(profile));
        }

        if (profile.BoardCert is { } bc)
        {
            if (string.IsNullOrWhiteSpace(bc.Specialty))
                throw new ArgumentException("BoardCert.Specialty is required.", nameof(profile));
            RequireIssueDate(bc.IssueDate, today, "BoardCert.IssueDate", nameof(profile));
            RequireExpiryDate(bc.ExpiryDate, maxExpiry, "BoardCert.ExpiryDate", nameof(profile));
            if (bc.IssueDate > bc.ExpiryDate)
                throw new ArgumentException(
                    $"BoardCert.IssueDate ({bc.IssueDate}) after ExpiryDate ({bc.ExpiryDate}).",
                    nameof(profile));
        }

        if (profile.Sanctions is { } s)
        {
            // One-day skew so a check stamped just after UTC midnight by a worker on
            // local clock doesn't trip the ceiling.
            var ceiling = nowUtc.AddDays(1);
            if (s.CheckedAt > ceiling || DateOnly.FromDateTime(s.CheckedAt.UtcDateTime) <= MinPlausibleDate)
                throw new ArgumentException(
                    $"Sanctions.CheckedAt implausible: {s.CheckedAt:O}.", nameof(profile));
        }
    }

    private static void RequirePlausible(DateOnly date, string field, string paramName)
    {
        if (date <= MinPlausibleDate)
            throw new ArgumentException($"{field} implausibly early: {date}.", paramName);
    }

    private static void RequireIssueDate(DateOnly date, DateOnly today, string field, string paramName)
    {
        RequirePlausible(date, field, paramName);
        if (date > today)
            throw new ArgumentException($"{field} in the future: {date}.", paramName);
    }

    private static void RequireExpiryDate(DateOnly date, DateOnly maxExpiry, string field, string paramName)
    {
        if (date <= MinPlausibleDate)
            throw new ArgumentException($"{field} implausibly early: {date}.", paramName);
        if (date > maxExpiry)
            throw new ArgumentException(
                $"{field} implausibly far in the future: {date} (max {maxExpiry}).", paramName);
    }
}
