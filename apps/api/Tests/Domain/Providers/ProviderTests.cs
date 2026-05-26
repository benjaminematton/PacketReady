using System.Text.Json;
using PacketReady.Domain;
using PacketReady.Domain.Providers;
using Xunit;

namespace PacketReady.Tests.Domain.Providers;

public class ProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);

    private static ProviderProfile MakeProfile(string npi = "1234567890") =>
        ProviderProfile.Create(
            fullName: "Dr. Jane Smith",
            dateOfBirth: new DateOnly(1980, 5, 15),
            npi: npi,
            credentialingState: "CA",
            nowUtc: Now);

    [Fact]
    public void Create_RejectsNullProfile()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Provider.Create(null!, Now));
        Assert.Equal("profile", ex.ParamName);
    }

    [Fact]
    public void Create_PopulatesIdAndCreatedAt()
    {
        var provider = Provider.Create(MakeProfile(), Now);

        Assert.NotEqual(Guid.Empty, provider.Id);
        Assert.Equal(Now, provider.CreatedAt);
    }

    [Fact]
    public void Create_DefaultsCreatedAtToUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var provider = Provider.Create(MakeProfile());
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(provider.CreatedAt, before, after);
    }

    [Fact]
    public void Create_SerializesProfileToJsonRoundTrippable()
    {
        var original = MakeProfile(npi: "5555555555");
        var provider = Provider.Create(original, Now);

        var roundTripped = provider.GetProfile();
        Assert.Equal(original.Npi, roundTripped.Npi);
        Assert.Equal(original.FullName, roundTripped.FullName);
        Assert.Equal(original.DateOfBirth, roundTripped.DateOfBirth);
    }

    [Fact]
    public void Create_PersistsEnumsAsStringsInJson()
    {
        // Proves DomainJson.Options (JsonStringEnumConverter) is on the write path.
        var license = new LicenseInfo("L1", "CA",
            new DateOnly(2020, 1, 1), new DateOnly(2030, 1, 1), LicenseStatus.Active);
        var profile = ProviderProfile.Create(
            fullName: "Dr. Jane Smith",
            dateOfBirth: new DateOnly(1980, 5, 15),
            npi: "1234567890",
            credentialingState: "CA",
            nowUtc: Now,
            license: license);

        var provider = Provider.Create(profile, Now);

        Assert.Contains("\"Active\"", provider.ProfileJson);
        Assert.DoesNotContain("\"Status\":1", provider.ProfileJson);
    }

    [Fact]
    public void Create_DefaultsPayerIdWhenNull()
    {
        var provider = Provider.Create(MakeProfile(), Now);
        Assert.Equal(Provider.DefaultPayerId, provider.PayerId);
    }

    [Fact]
    public void Create_UsesExplicitPayerId()
    {
        var provider = Provider.Create(MakeProfile(), Now, payerId: "payer-b-regional-ppo");
        Assert.Equal("payer-b-regional-ppo", provider.PayerId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsWhitespacePayerId(string payerId)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Provider.Create(MakeProfile(), Now, payerId));
        Assert.Equal("payerId", ex.ParamName);
    }

    [Fact]
    public void GetProfile_ReturnsCachedInstanceOnSecondCall()
    {
        var provider = Provider.Create(MakeProfile(), Now);

        var first = provider.GetProfile();
        var second = provider.GetProfile();

        Assert.Same(first, second);
    }

    [Fact]
    public void ProfileJson_SetterInvalidatesCache()
    {
        // Simulates EF Core rehydrating the entity: the property setter fires, which
        // must clear the cached ProviderProfile so the next GetProfile() reflects the
        // new JSON.
        var provider = Provider.Create(MakeProfile(npi: "1111111111"), Now);
        var first = provider.GetProfile();
        Assert.Equal("1111111111", first.Npi);

        var newJson = JsonSerializer.Serialize(MakeProfile(npi: "9999999999"), DomainJson.Options);
        typeof(Provider).GetProperty(nameof(Provider.ProfileJson))!
            .SetValue(provider, newJson);

        var second = provider.GetProfile();
        Assert.NotSame(first, second);
        Assert.Equal("9999999999", second.Npi);
    }

    [Fact]
    public void GetProfile_ThrowsOnCorruptJson()
    {
        var provider = Provider.Create(MakeProfile(), Now);

        typeof(Provider).GetProperty(nameof(Provider.ProfileJson))!
            .SetValue(provider, "not valid json");

        var ex = Assert.Throws<JsonException>(() => provider.GetProfile());
        Assert.NotNull(ex);
    }

    // ──────────────────────────────────────── IntakeBudgetTurns ──────────

    [Fact]
    public void Create_DefaultsIntakeBudgetTurnsTo8()
    {
        var provider = Provider.Create(MakeProfile(), Now);
        Assert.Equal(8, provider.IntakeBudgetTurns);
        Assert.Equal(8, Provider.DefaultIntakeBudgetTurns);
    }

    [Fact]
    public void Create_HonorsExplicitIntakeBudgetTurns()
    {
        var provider = Provider.Create(
            MakeProfile(), Now, payerId: null, intakeBudgetTurns: 12);
        Assert.Equal(12, provider.IntakeBudgetTurns);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_RejectsNonPositiveIntakeBudgetTurns(int budget)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Provider.Create(MakeProfile(), Now, payerId: null, intakeBudgetTurns: budget));
        Assert.Equal("intakeBudgetTurns", ex.ParamName);
    }
}
