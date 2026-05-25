using Microsoft.Extensions.Time.Testing;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;
using Xunit;
using static PacketReady.Tests.Application.Scoring.Validators.TestProfiles;

namespace PacketReady.Tests.Application.Scoring.Validators;

public sealed class DeaStatusValidatorTests
{
    private static DeaStatusValidator Build() =>
        new(new FakeTimeProvider(DateTimeOffset.Parse(Today)));

    [Fact]
    public async Task ValidDea_EmitsEmpty()
    {
        var issues = await Build().RunAsync(MakeProfile(), default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task NoDea_ShortCircuits()
    {
        // Missing-DEA is owned by the aggregator; this validator stays silent
        // to avoid double-counting Criticals.
        var profile = MakeProfile() with { Dea = null };
        var issues = await Build().RunAsync(profile, default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task InactiveStatus_EmitsCritical()
    {
        var profile = MakeProfile(dea: MakeDea(status: DeaStatus.Inactive));
        var issues = await Build().RunAsync(profile, default);
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Critical, only.Severity);
        Assert.Contains("Inactive", only.Message);
    }

    [Fact]
    public async Task Expired_EmitsCritical()
    {
        var profile = MakeProfile(dea: MakeDea(expiryDate: TodayDate.AddDays(-1)));
        var issues = await Build().RunAsync(profile, default);
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Critical, only.Severity);
        Assert.Contains("expired", only.Message);
    }

    [Fact]
    public async Task RenewalWindow_EmitsMinor()
    {
        var profile = MakeProfile(dea: MakeDea(expiryDate: TodayDate.AddDays(20)));
        var issues = await Build().RunAsync(profile, default);
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Minor, only.Severity);
        Assert.Contains("20 days", only.Message);
    }

    [Fact]
    public async Task RenewalWindow_Boundary_ThirtyDays_EmitsEmpty()
    {
        // Renewal window is `< 30` days; exactly 30 days out is outside the window.
        var profile = MakeProfile(dea: MakeDea(expiryDate: TodayDate.AddDays(30)));
        var issues = await Build().RunAsync(profile, default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task ExpiresToday_IsValid_OnlyMinor()
    {
        // Same industry-convention boundary as License.
        var profile = MakeProfile(dea: MakeDea(expiryDate: TodayDate));
        var issues = await Build().RunAsync(profile, default);
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Minor, only.Severity);
    }

    [Fact]
    public async Task EveryIssue_CarriesValidatorName()
    {
        var profile = MakeProfile(dea: MakeDea(status: DeaStatus.Inactive, expiryDate: TodayDate.AddDays(-1)));
        var issues = await Build().RunAsync(profile, default);
        Assert.All(issues, i => Assert.Equal("dea_status", i.Validator));
    }
}
