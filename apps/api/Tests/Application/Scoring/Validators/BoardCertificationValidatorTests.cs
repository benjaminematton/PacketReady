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
        new(new FakeTimeProvider(DateTimeOffset.Parse(Today)), MakePayers());

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

    // === P4: payer-config branches =========================================

    [Fact]
    public async Task PayerB_BoardCertNotRequired_ShortCircuits()
    {
        // payer-b has BoardCertRequired=false. The validator returns empty
        // regardless of the cert's status / expiry / board — the payer
        // doesn't gate on board cert, period. Pins the defensive guard.
        var v = new BoardCertificationValidator(
            new FakeTimeProvider(DateTimeOffset.Parse(Today)),
            MakePayers());

        var expiredProfile = MakeProfile(
            boardCert: MakeBoardCert(status: BoardCertStatus.Expired, board: "ABIM"));
        var issues = await v.RunAsync(
            expiredProfile, new Dictionary<string, PacketReady.Application.Providers.Aggregation.FieldProvenance>(),
            "payer-b-state-medicaid", default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task PayerA_AcceptsABMSMemberBoard_NoExtraIssue()
    {
        // payer-a's accepted-board list enumerates real ABMS member-board
        // acronyms (no umbrella "ABMS" — the umbrella body doesn't issue
        // certs). A cert from ABIM (the most common member board) passes.
        var v = new BoardCertificationValidator(
            new FakeTimeProvider(DateTimeOffset.Parse(Today)),
            MakePayers());
        var profile = MakeProfile(boardCert: MakeBoardCert(board: "ABIM"));

        var issues = await v.RunAsync(
            profile, new Dictionary<string, PacketReady.Application.Providers.Aggregation.FieldProvenance>(),
            "payer-a-national-hmo", default);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task PayerA_AcceptsABMS_RejectsAOA()
    {
        // payer-a accepts ABMS member boards. A board cert from AOA (American
        // Osteopathic Association) lands a Major — payer negotiation, not
        // a hard block.
        var v = new BoardCertificationValidator(
            new FakeTimeProvider(DateTimeOffset.Parse(Today)),
            MakePayers());
        var profile = MakeProfile(boardCert: MakeBoardCert(board: "AOA"));

        var issues = await v.RunAsync(
            profile, new Dictionary<string, PacketReady.Application.Providers.Aggregation.FieldProvenance>(),
            "payer-a-national-hmo", default);

        var only = Assert.Single(issues);
        Assert.Equal(Severity.Major, only.Severity);
        Assert.Contains("AOA", only.Message);
        Assert.Contains("Payer A", only.Message);
    }

    [Fact]
    public async Task PayerA_AcceptsNicheABMSMemberBoards_NoExtraIssue()
    {
        // The dataset has packets using ABD/ABTS/ABU (Dermatology, Thoracic
        // Surgery, Urology) — real ABMS member boards that were missing from
        // the prior YAML and falsely flagged Major. Pins the expanded list.
        var v = new BoardCertificationValidator(
            new FakeTimeProvider(DateTimeOffset.Parse(Today)),
            MakePayers());

        foreach (var board in new[] { "ABD", "ABTS", "ABU" })
        {
            var profile = MakeProfile(boardCert: MakeBoardCert(board: board));
            var issues = await v.RunAsync(
                profile, new Dictionary<string, PacketReady.Application.Providers.Aggregation.FieldProvenance>(),
                "payer-a-national-hmo", default);
            Assert.Empty(issues);
        }
    }

    [Fact]
    public async Task StatusCritical_CitesStatusField_NotExpiryField()
    {
        var v = new BoardCertificationValidator(
            new FakeTimeProvider(DateTimeOffset.Parse(Today)),
            MakePayers());
        var profile = MakeProfile(boardCert: MakeBoardCert(status: BoardCertStatus.Expired));

        var issues = await v.RunAsync(
            profile, new Dictionary<string, PacketReady.Application.Providers.Aggregation.FieldProvenance>(),
            "payer-a-national-hmo", default);

        var only = Assert.Single(issues);
        var cite = Assert.Single(only.Citations);
        Assert.Contains("status=", cite.ExtractedValue, StringComparison.Ordinal);
        Assert.DoesNotContain("expires=", cite.ExtractedValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownPayerId_Throws()
    {
        var v = new BoardCertificationValidator(
            new FakeTimeProvider(DateTimeOffset.Parse(Today)),
            MakePayers());

        await Assert.ThrowsAsync<PacketReady.Application.Payers.PayerNotConfiguredException>(() =>
            v.RunAsync(
                MakeProfile(),
                new Dictionary<string, PacketReady.Application.Providers.Aggregation.FieldProvenance>(),
                "payer-c-doesnt-exist", default));
    }
}
