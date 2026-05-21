using PacketReady.Domain.Providers;
using Xunit;

namespace PacketReady.Tests.Domain.Providers;

public class ProviderProfileTests
{
    // Fixed clock anchor so date-relative tests don't drift with wall time.
    private static readonly DateTimeOffset Now = new(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private static ProviderProfile ValidGolden(
        string npi = "1234567890",
        string state = "CA",
        LicenseInfo? license = null,
        DeaInfo? dea = null,
        BoardCertInfo? boardCert = null,
        SanctionsResult? sanctions = null) =>
        ProviderProfile.Create(
            fullName: "Dr. Jane Smith",
            dateOfBirth: new DateOnly(1980, 5, 15),
            npi: npi,
            credentialingState: state,
            nowUtc: Now,
            license: license,
            dea: dea,
            boardCert: boardCert,
            sanctions: sanctions);

    [Theory]
    [InlineData("")]
    [InlineData("12345")]        // too short
    [InlineData("12345678901")]  // too long
    [InlineData("abcdefghij")]   // non-digit
    [InlineData("123-456-789")]  // dashes
    public void Create_RejectsInvalidNpi(string badNpi)
    {
        var ex = Assert.Throws<ArgumentException>(() => ValidGolden(npi: badNpi));
        Assert.Equal("profile", ex.ParamName);
        Assert.Contains("Npi", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ca")]    // lowercase
    [InlineData("CAL")]   // too long
    [InlineData("C")]     // too short
    [InlineData("12")]    // digits
    public void Create_RejectsInvalidState(string badState)
    {
        var ex = Assert.Throws<ArgumentException>(() => ValidGolden(state: badState));
        Assert.Equal("profile", ex.ParamName);
        Assert.Contains("CredentialingState", ex.Message);
    }

    [Fact]
    public void Create_RejectsFutureDateOfBirth()
    {
        var ex = Assert.Throws<ArgumentException>(() => ProviderProfile.Create(
            fullName: "J",
            dateOfBirth: Today.AddDays(1),
            npi: "1234567890",
            credentialingState: "CA",
            nowUtc: Now));
        Assert.Contains("DateOfBirth in the future", ex.Message);
    }

    [Fact]
    public void Create_RejectsImplausiblyEarlyDateOfBirth()
    {
        var ex = Assert.Throws<ArgumentException>(() => ProviderProfile.Create(
            fullName: "J",
            dateOfBirth: new DateOnly(1899, 12, 31),
            npi: "1234567890",
            credentialingState: "CA",
            nowUtc: Now));
        Assert.Contains("DateOfBirth implausibly early", ex.Message);
    }

    [Fact]
    public void Create_RejectsLicenseExpiryFarBeyondCeiling()
    {
        var license = new LicenseInfo(
            Number: "L1",
            State: "CA",
            IssueDate: new DateOnly(2020, 1, 1),
            ExpiryDate: Today.AddYears(51),
            Status: LicenseStatus.Active);

        var ex = Assert.Throws<ArgumentException>(() => ValidGolden(license: license));
        Assert.Contains("License.ExpiryDate implausibly far", ex.Message);
    }

    [Fact]
    public void Create_RejectsLicenseIssueAfterExpiry()
    {
        var license = new LicenseInfo(
            Number: "L1",
            State: "CA",
            IssueDate: new DateOnly(2025, 6, 1),
            ExpiryDate: new DateOnly(2024, 6, 1),
            Status: LicenseStatus.Active);

        var ex = Assert.Throws<ArgumentException>(() => ValidGolden(license: license));
        Assert.Contains("License.IssueDate", ex.Message);
        Assert.Contains("after ExpiryDate", ex.Message);
    }

    [Fact]
    public void Create_RejectsBoardCertIssueAfterExpiry()
    {
        var bc = new BoardCertInfo(
            Board: "ABIM",
            Specialty: "Internal Medicine",
            IssueDate: new DateOnly(2025, 6, 1),
            ExpiryDate: new DateOnly(2024, 6, 1),
            Status: BoardCertStatus.Active);

        var ex = Assert.Throws<ArgumentException>(() => ValidGolden(boardCert: bc));
        Assert.Contains("BoardCert.IssueDate", ex.Message);
    }

    [Fact]
    public void Create_RejectsSanctionsCheckedAtBeyondCeiling()
    {
        var sanctions = new SanctionsResult(
            OigClean: true,
            SamClean: true,
            CheckedAt: Now.AddDays(2));

        var ex = Assert.Throws<ArgumentException>(() => ValidGolden(sanctions: sanctions));
        Assert.Contains("Sanctions.CheckedAt implausible", ex.Message);
    }

    [Fact]
    public void Create_AllowsSanctionsCheckedAtWithinOneDaySkew()
    {
        var sanctions = new SanctionsResult(
            OigClean: true,
            SamClean: true,
            CheckedAt: Now.AddHours(23));

        var profile = ValidGolden(sanctions: sanctions);
        Assert.NotNull(profile.Sanctions);
        Assert.Equal(Now.AddHours(23), profile.Sanctions!.CheckedAt);
    }

    [Fact]
    public void Create_ReturnsValidProfileForGoldenInput()
    {
        var license = new LicenseInfo("ABC123", "CA",
            new DateOnly(2020, 1, 1), new DateOnly(2030, 1, 1), LicenseStatus.Active);
        var dea = new DeaInfo("BS1234567", new DateOnly(2028, 6, 1), DeaStatus.Active,
            new[] { DeaSchedule.II, DeaSchedule.III, DeaSchedule.IV, DeaSchedule.V });
        var bc = new BoardCertInfo("ABIM", "Internal Medicine",
            new DateOnly(2015, 6, 1), new DateOnly(2035, 6, 1), BoardCertStatus.Active);
        var sanctions = new SanctionsResult(true, true, Now);

        var p = ProviderProfile.Create(
            fullName: "Dr. Jane Smith",
            dateOfBirth: new DateOnly(1980, 5, 15),
            npi: "1234567890",
            credentialingState: "CA",
            nowUtc: Now,
            license: license, dea: dea, boardCert: bc, sanctions: sanctions);

        Assert.Equal("Dr. Jane Smith", p.FullName);
        Assert.Equal("1234567890", p.Npi);
        Assert.Equal("CA", p.CredentialingState);
        Assert.Equal(license, p.License);
        Assert.Equal(bc, p.BoardCert);
        Assert.Equal(sanctions, p.Sanctions);
    }

    [Fact]
    public void Validate_RejectsNullProfile()
    {
        Assert.Throws<ArgumentNullException>(() => ProviderProfile.Validate(null!, Now));
    }

    [Fact]
    public void WithExpression_BypassesValidation_ProviderCreateStillCatchesIt()
    {
        // `with` uses the compiler-generated copy ctor, so it bypasses ProviderProfile.Create.
        // The defense-in-depth Validate inside Provider.Create is what catches the corruption.
        var valid = ValidGolden();
        var corrupted = valid with { Npi = "bad" };

        var ex = Assert.Throws<ArgumentException>(() => Provider.Create(corrupted, Now));
        Assert.Contains("Npi", ex.Message);
    }
}
