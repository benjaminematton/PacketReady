using PacketReady.Application.Scoring;
using PacketReady.Domain.Scoring;
using Xunit;

namespace PacketReady.Tests.Application.Scoring;

public sealed class ScoreSynthesizerTests
{
    private static Issue I(Severity sev) =>
        new("test_validator", sev, "msg", "rem", Array.Empty<Citation>());

    [Fact]
    public void Empty_ReturnsHundred()
    {
        Assert.Equal(100, ScoreSynthesizer.Compute(Array.Empty<Issue>()));
    }

    [Theory]
    [InlineData(Severity.Critical, 75)]
    [InlineData(Severity.Major, 90)]
    [InlineData(Severity.Minor, 97)]
    public void OneIssue_AppliesCorrectPenalty(Severity sev, int expected)
    {
        Assert.Equal(expected, ScoreSynthesizer.Compute(new[] { I(sev) }));
    }

    [Fact]
    public void YellowFixtureMix_Equals62()
    {
        // 1 Critical + 1 Major + 1 Minor — matches the yellow fixture in the design doc.
        var issues = new[] { I(Severity.Critical), I(Severity.Major), I(Severity.Minor) };
        Assert.Equal(62, ScoreSynthesizer.Compute(issues));
    }

    [Fact]
    public void RedFixtureMix_Equals34()
    {
        // 2 Critical + 1 Major + 2 Minor — matches the red fixture in the design doc.
        var issues = new[]
        {
            I(Severity.Critical), I(Severity.Critical),
            I(Severity.Major),
            I(Severity.Minor), I(Severity.Minor),
        };
        Assert.Equal(34, ScoreSynthesizer.Compute(issues));
    }

    [Fact]
    public void FourCriticals_ScoresExactlyZero()
    {
        // 100 − 4×25 = 0 — boundary case, hit by computation not by clamping.
        var issues = Enumerable.Repeat(I(Severity.Critical), 4).ToArray();
        Assert.Equal(0, ScoreSynthesizer.Compute(issues));
    }

    [Fact]
    public void OverflowingPenalties_FlooredAtZero()
    {
        // Five Criticals would compute to -25; floor must clamp.
        var issues = Enumerable.Repeat(I(Severity.Critical), 5).ToArray();
        Assert.Equal(0, ScoreSynthesizer.Compute(issues));
    }

    [Fact]
    public void FourCriticalsPlusMinor_FlooredAtZero()
    {
        // 100 − 100 − 3 = -3 → clamped to 0.
        var issues = new List<Issue>(Enumerable.Repeat(I(Severity.Critical), 4)) { I(Severity.Minor) };
        Assert.Equal(0, ScoreSynthesizer.Compute(issues));
    }

    [Fact]
    public void PenaltyConstants_MatchRubric()
    {
        // Locks the rubric values against accidental tuning. An unintentional
        // bump fails here; an intentional revision updates both sides.
        Assert.Equal(25, ScoreSynthesizer.CriticalPenalty);
        Assert.Equal(10, ScoreSynthesizer.MajorPenalty);
        Assert.Equal(3, ScoreSynthesizer.MinorPenalty);
    }

    [Fact]
    public void UnknownSeverity_Throws()
    {
        // Guards against corrupted JSONB rows or future enum values added without
        // wiring a penalty here — a silent zero would inflate provider scores.
        var bogus = new Issue("test_validator", (Severity)999, "msg", "rem", Array.Empty<Citation>());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScoreSynthesizer.Compute(new[] { bogus }));
    }

    [Fact]
    public void Compute_IgnoresNonSeverityFields()
    {
        // Pins the contract that only Severity feeds the score — Validator name,
        // Message, Remediation, and Citations are display concerns.
        var bare = new Issue("v", Severity.Major, "", "", Array.Empty<Citation>());
        var rich = new Issue(
            "different_validator",
            Severity.Major,
            "long message",
            "long remediation",
            new[] { new Citation("v1", "value-a"), new Citation("v2", "value-b") });
        Assert.Equal(
            ScoreSynthesizer.Compute(new[] { bare }),
            ScoreSynthesizer.Compute(new[] { rich }));
    }
}
