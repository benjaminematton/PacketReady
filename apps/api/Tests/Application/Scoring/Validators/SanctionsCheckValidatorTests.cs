using Microsoft.Extensions.Time.Testing;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;
using Xunit;
using static PacketReady.Tests.Application.Scoring.Validators.TestProfiles;

namespace PacketReady.Tests.Application.Scoring.Validators;

public sealed class SanctionsCheckValidatorTests
{
    private static SanctionsCheckValidator Build() =>
        new(new FakeTimeProvider(DateTimeOffset.Parse(Today)));

    private static DateTimeOffset DaysAgo(int days) =>
        DateTimeOffset.Parse(Today).AddDays(-days);

    [Fact]
    public async Task FreshAndClean_EmitsEmpty()
    {
        var profile = MakeProfile(sanctions: MakeSanctions(checkedAt: DaysAgo(7)));
        var issues = await Build().RunAsync(profile, default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task NoSanctions_EmitsCritical()
    {
        var profile = MakeProfile() with { Sanctions = null };
        var issues = await Build().RunAsync(profile, default);
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Critical, only.Severity);
        Assert.Contains("No sanctions", only.Message);
    }

    [Fact]
    public async Task OigNotClean_EmitsOneCritical_CitesOig()
    {
        var profile = MakeProfile(sanctions: MakeSanctions(oigClean: false));
        var issues = await Build().RunAsync(profile, default);

        var only = Assert.Single(issues);
        Assert.Equal(Severity.Critical, only.Severity);
        Assert.Contains("OIG", only.Message);
        Assert.All(only.Citations, c => Assert.Contains("OIG", c.ExtractedValue));
    }

    [Fact]
    public async Task SamNotClean_EmitsOneCritical_CitesSam()
    {
        var profile = MakeProfile(sanctions: MakeSanctions(samClean: false));
        var issues = await Build().RunAsync(profile, default);

        var only = Assert.Single(issues);
        Assert.Equal(Severity.Critical, only.Severity);
        Assert.Contains("SAM", only.Message);
        Assert.All(only.Citations, c => Assert.Contains("SAM", c.ExtractedValue));
    }

    [Fact]
    public async Task BothNotClean_EmitsTwoCriticals()
    {
        var profile = MakeProfile(sanctions: MakeSanctions(oigClean: false, samClean: false));
        var issues = await Build().RunAsync(profile, default);

        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(Severity.Critical, i.Severity));
        Assert.Contains(issues, i => i.Message.Contains("OIG"));
        Assert.Contains(issues, i => i.Message.Contains("SAM"));
    }

    [Fact]
    public async Task CleanButStale_OverNinetyDays_EmitsTwoMinors()
    {
        // The red-fixture case: clean, but the single CheckedAt is stale → per-source Minor.
        var profile = MakeProfile(sanctions: MakeSanctions(checkedAt: DaysAgo(100)));
        var issues = await Build().RunAsync(profile, default);

        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(Severity.Minor, i.Severity));
        Assert.Contains(issues, i => i.Message.Contains("OIG"));
        Assert.Contains(issues, i => i.Message.Contains("SAM"));
    }

    [Fact]
    public async Task CleanButStale_OverOneYear_EmitsTwoMajors()
    {
        var profile = MakeProfile(sanctions: MakeSanctions(checkedAt: DaysAgo(400)));
        var issues = await Build().RunAsync(profile, default);

        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(Severity.Major, i.Severity));
    }

    [Fact]
    public async Task NotClean_StalenessSuppressed_OnlyCritical()
    {
        // OIG dirty + CheckedAt stale: the Critical is the headline; staleness Minor
        // for OIG is suppressed. SAM is clean and stale → Minor for SAM only.
        var profile = MakeProfile(sanctions: MakeSanctions(oigClean: false, samClean: true, checkedAt: DaysAgo(100)));
        var issues = await Build().RunAsync(profile, default);

        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Severity == Severity.Critical && i.Message.Contains("OIG"));
        Assert.Contains(issues, i => i.Severity == Severity.Minor && i.Message.Contains("SAM"));
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Minor && i.Message.Contains("OIG"));
    }

    [Fact]
    public async Task EightyNineDays_StillFresh_EmitsEmpty()
    {
        // Boundary: 89 days < 90 → no Minor.
        var profile = MakeProfile(sanctions: MakeSanctions(checkedAt: DaysAgo(89)));
        var issues = await Build().RunAsync(profile, default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task ExactlyNinetyDays_EmitsTwoMinors()
    {
        // Boundary: 90 days hits the `>= 90` Minor threshold. Locks against a `> 90` regression.
        var profile = MakeProfile(sanctions: MakeSanctions(checkedAt: DaysAgo(90)));
        var issues = await Build().RunAsync(profile, default);

        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(Severity.Minor, i.Severity));
    }

    [Fact]
    public async Task ExactlyThreeSixtyFiveDays_EmitsTwoMajors()
    {
        // Boundary: 365 days hits the `>= 365` Major threshold. Locks against a `> 365` regression.
        var profile = MakeProfile(sanctions: MakeSanctions(checkedAt: DaysAgo(365)));
        var issues = await Build().RunAsync(profile, default);

        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(Severity.Major, i.Severity));
    }

    [Fact]
    public async Task EveryIssue_CarriesValidatorName()
    {
        var profile = MakeProfile(sanctions: MakeSanctions(oigClean: false, samClean: false, checkedAt: DaysAgo(400)));
        var issues = await Build().RunAsync(profile, default);
        Assert.All(issues, i => Assert.Equal("sanctions_check", i.Validator));
    }
}
