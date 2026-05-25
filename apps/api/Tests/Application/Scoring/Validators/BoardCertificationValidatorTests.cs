using Microsoft.Extensions.Time.Testing;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;
using Xunit;
using static PacketReady.Tests.Application.Scoring.Validators.TestProfiles;

namespace PacketReady.Tests.Application.Scoring.Validators;

public sealed class BoardCertificationValidatorTests
{
    private static BoardCertificationValidator Build() =>
        new(new FakeTimeProvider(DateTimeOffset.Parse(Today)));

    [Fact]
    public async Task ValidBoardCert_EmitsEmpty()
    {
        var issues = await Build().RunAsync(MakeProfile(), default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task NoBoardCert_ShortCircuits()
    {
        // Missing-board-cert is owned by the aggregator; this validator stays
        // silent to avoid double-counting Criticals.
        var profile = MakeProfile() with { BoardCert = null };
        var issues = await Build().RunAsync(profile, default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task ExpiredStatus_EmitsCritical()
    {
        var profile = MakeProfile(boardCert: MakeBoardCert(status: BoardCertStatus.Expired));
        var issues = await Build().RunAsync(profile, default);
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Critical, only.Severity);
        Assert.Contains("Expired", only.Message);
    }

    [Fact]
    public async Task ExpiredDate_EmitsCritical()
    {
        // Status stays Active to isolate the expiry branch.
        var profile = MakeProfile(boardCert: MakeBoardCert(expiryDate: TodayDate.AddDays(-1)));
        var issues = await Build().RunAsync(profile, default);
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Critical, only.Severity);
        Assert.Contains("expired", only.Message);
    }

    [Fact]
    public async Task RenewalWindow_EmitsMinor()
    {
        var profile = MakeProfile(boardCert: MakeBoardCert(expiryDate: TodayDate.AddDays(29)));
        var issues = await Build().RunAsync(profile, default);
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Minor, only.Severity);
        Assert.Contains("29 days", only.Message);
    }

    [Fact]
    public async Task ExpiresToday_IsValid_OnlyMinor()
    {
        // Same industry-convention boundary as License/DEA: expiry inclusive.
        var profile = MakeProfile(boardCert: MakeBoardCert(expiryDate: TodayDate));
        var issues = await Build().RunAsync(profile, default);
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Minor, only.Severity);
    }

    [Fact]
    public async Task RenewalWindow_Boundary_ThirtyDays_EmitsEmpty()
    {
        // Renewal window is `< 30` days; exactly 30 days out is outside the window.
        var profile = MakeProfile(boardCert: MakeBoardCert(expiryDate: TodayDate.AddDays(30)));
        var issues = await Build().RunAsync(profile, default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task EveryIssue_CarriesValidatorName()
    {
        var profile = MakeProfile(
            boardCert: MakeBoardCert(status: BoardCertStatus.Expired, expiryDate: TodayDate.AddDays(-1)));
        var issues = await Build().RunAsync(profile, default);
        Assert.All(issues, i => Assert.Equal("board_certification", i.Validator));
    }
}
