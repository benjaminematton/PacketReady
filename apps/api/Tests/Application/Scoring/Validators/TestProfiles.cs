using PacketReady.Domain.Providers;

namespace PacketReady.Tests.Application.Scoring.Validators;

/// <summary>
/// Shared builders for validator unit tests. Each <c>Make*</c> defaults to a valid
/// instance (active, expires far in the future, etc.). Tests override the field
/// they're exercising and leave the rest at the happy-path default.
/// </summary>
internal static class TestProfiles
{
    public const string Today = "2026-06-01T00:00:00Z";
    public static readonly DateOnly TodayDate = new(2026, 6, 1);

    /// <summary>
    /// Happy-path profile builder. To exercise "no X" branches, call
    /// <c>MakeProfile() with { X = null }</c> — `with` bypasses shape validation
    /// (which is what we want; the validator under test treats null as "no X on
    /// file" and emits the appropriate Issue).
    /// </summary>
    public static ProviderProfile MakeProfile(
        LicenseInfo? license = null,
        DeaInfo? dea = null,
        BoardCertInfo? boardCert = null,
        SanctionsResult? sanctions = null,
        string credentialingState = "NY")
    {
        return ProviderProfile.Create(
            fullName: "Dr. Test Person",
            dateOfBirth: new DateOnly(1980, 1, 1),
            npi: "1234567890",
            credentialingState: credentialingState,
            nowUtc: DateTimeOffset.Parse(Today),
            license: license ?? MakeLicense(),
            dea: dea ?? MakeDea(),
            boardCert: boardCert ?? MakeBoardCert(),
            sanctions: sanctions ?? MakeSanctions());
    }

    public static LicenseInfo MakeLicense(
        string number = "MD123456",
        string state = "NY",
        DateOnly? issueDate = null,
        DateOnly? expiryDate = null,
        LicenseStatus status = LicenseStatus.Active)
        => new(number, state, issueDate ?? TodayDate.AddYears(-5), expiryDate ?? TodayDate.AddYears(2), status);

    public static DeaInfo MakeDea(
        string number = "BD1234567",
        DateOnly? expiryDate = null,
        DeaStatus status = DeaStatus.Active,
        IReadOnlyList<DeaSchedule>? schedules = null)
        => new(number, expiryDate ?? TodayDate.AddYears(2), status,
               schedules ?? new[] { DeaSchedule.II, DeaSchedule.III, DeaSchedule.IV, DeaSchedule.V });

    public static BoardCertInfo MakeBoardCert(
        string board = "ABIM",
        string specialty = "Internal Medicine",
        DateOnly? issueDate = null,
        DateOnly? expiryDate = null,
        BoardCertStatus status = BoardCertStatus.Active)
        => new(board, specialty, issueDate ?? TodayDate.AddYears(-3), expiryDate ?? TodayDate.AddYears(5), status);

    public static SanctionsResult MakeSanctions(
        bool oigClean = true,
        bool samClean = true,
        DateTimeOffset? checkedAt = null)
        => new(oigClean, samClean, checkedAt ?? DateTimeOffset.Parse(Today).AddDays(-7));
}
