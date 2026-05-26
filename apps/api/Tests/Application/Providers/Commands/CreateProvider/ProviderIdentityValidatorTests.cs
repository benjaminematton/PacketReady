using PacketReady.Application.Providers.Commands.CreateProvider;
using Xunit;

namespace PacketReady.Tests.Application.Providers.Commands.CreateProvider;

public sealed class ProviderIdentityValidatorTests
{
    private static readonly DateOnly Today = new(2026, 5, 26);

    private static ProviderIdentityDto Valid(
        string? fullName = null,
        string? npi = null,
        DateOnly? dob = null,
        string? state = null)
        => new(
            FullName: fullName ?? "Henry Anderson, MD",
            Npi: npi ?? "1234567893",
            DateOfBirth: dob ?? new DateOnly(1980, 1, 1),
            CredentialingState: state ?? "NY");

    [Fact]
    public void Placeholder_IsLuhnValid_AndPassesValidator()
    {
        Assert.True(ProviderIdentityValidator.IsNpiLuhnValid(
            ProviderIdentityValidator.Placeholder.Npi));
        Assert.Empty(ProviderIdentityValidator.Validate(
            ProviderIdentityValidator.Placeholder, Today));
    }

    [Fact]
    public void HappyPath_NoViolations()
    {
        var violations = ProviderIdentityValidator.Validate(Valid(), Today);
        Assert.Empty(violations);
    }

    // === NPI ==================================================================

    [Theory]
    [InlineData("12345")]
    [InlineData("12345678901")]
    [InlineData("")]
    public void Npi_WrongLength_OneViolation(string npi)
    {
        var violations = ProviderIdentityValidator.Validate(Valid(npi: npi), Today);
        Assert.Single(violations);
        Assert.Contains("10 digits", violations[0]);
    }

    [Fact]
    public void Npi_NonDigit_OneViolation()
    {
        var violations = ProviderIdentityValidator.Validate(Valid(npi: "12345abcde"), Today);
        Assert.Single(violations);
        Assert.Contains("10 digits", violations[0]);
    }

    [Fact]
    public void Npi_TenDigits_NonLuhn_OneViolation()
    {
        // 1234567890 fails the CMS Luhn check.
        var violations = ProviderIdentityValidator.Validate(Valid(npi: "1234567890"), Today);
        Assert.Single(violations);
        Assert.Contains("Luhn", violations[0]);
    }

    [Fact]
    public void Npi_LuhnValid_NoViolation()
    {
        // 1234567893 verified by hand against CMS "80840" prefix + standard
        // Luhn-mod-10 algorithm. Keep this on a single known-good value;
        // a parameterized list of "Luhn-valid" inputs is exactly the kind
        // of test that turns into hidden bugs when one entry drifts.
        var violations = ProviderIdentityValidator.Validate(
            Valid(npi: "1234567893"), Today);
        Assert.Empty(violations);
    }

    [Fact]
    public void IsNpiLuhnValid_NullOrWrongLength_ReturnsFalse()
    {
        Assert.False(ProviderIdentityValidator.IsNpiLuhnValid(null!));
        Assert.False(ProviderIdentityValidator.IsNpiLuhnValid(""));
        Assert.False(ProviderIdentityValidator.IsNpiLuhnValid("12345"));
    }

    // === State ================================================================

    [Theory]
    [InlineData("ny")]    // lowercase
    [InlineData("N")]     // single char
    [InlineData("NYC")]   // 3 chars
    [InlineData("N1")]    // digit
    [InlineData("")]
    public void State_OffShape_OneViolation(string state)
    {
        var violations = ProviderIdentityValidator.Validate(Valid(state: state), Today);
        Assert.Single(violations);
        Assert.Contains("credentialingState", violations[0]);
    }

    // === FullName =============================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FullName_Blank_OneViolation(string name)
    {
        var violations = ProviderIdentityValidator.Validate(Valid(fullName: name), Today);
        Assert.Single(violations);
        Assert.Contains("fullName", violations[0]);
    }

    // === DOB ==================================================================

    [Fact]
    public void Dob_TooEarly_OneViolation()
    {
        var violations = ProviderIdentityValidator.Validate(
            Valid(dob: new DateOnly(1850, 1, 1)), Today);
        Assert.Single(violations);
        Assert.Contains("dateOfBirth", violations[0]);
        Assert.Contains("1900", violations[0]);
    }

    [Fact]
    public void Dob_Future_OneViolation()
    {
        var violations = ProviderIdentityValidator.Validate(
            Valid(dob: Today.AddDays(1)), Today);
        Assert.Single(violations);
        Assert.Contains("future", violations[0]);
    }

    [Fact]
    public void Dob_ExactlyToday_NoViolation()
    {
        // Boundary: a DOB stamped today is plausible (a baby born today,
        // not credentialable but not a shape violation).
        var violations = ProviderIdentityValidator.Validate(
            Valid(dob: Today), Today);
        Assert.Empty(violations);
    }

    // === Multiple violations ==================================================

    [Fact]
    public void AllFourBad_FourViolations()
    {
        // The boundary contract is "list ALL violations, not just the
        // first" — exercise the multi-error path explicitly so a future
        // short-circuit refactor breaks this test.
        var bad = new ProviderIdentityDto(
            FullName: "",
            Npi: "abc",
            DateOfBirth: new DateOnly(1850, 1, 1),
            CredentialingState: "nyc");

        var violations = ProviderIdentityValidator.Validate(bad, Today);

        Assert.Equal(4, violations.Count);
        Assert.Contains(violations, v => v.Contains("fullName"));
        Assert.Contains(violations, v => v.Contains("10 digits"));
        Assert.Contains(violations, v => v.Contains("dateOfBirth"));
        Assert.Contains(violations, v => v.Contains("credentialingState"));
    }
}
