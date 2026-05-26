using PacketReady.Application.Scoring;
using PacketReady.Domain.Scoring;
using Xunit;

namespace PacketReady.Tests.Application.Scoring;

public sealed class ConfidenceGuardTests
{
    private static Issue MakeIssue(
        Severity severity,
        params bool[] citationLowConfidenceFlags)
    {
        var citations = citationLowConfidenceFlags
            .Select(lc => new Citation("test", "extracted") { LowConfidence = lc })
            .ToList();
        return new Issue("test", severity, "msg", "rem", citations);
    }

    [Fact]
    public void EmptyList_ReturnsEmpty()
    {
        Assert.Empty(ConfidenceGuard.Apply(Array.Empty<Issue>()));
    }

    [Fact]
    public void CriticalWithLowConfidenceCitation_DowngradesToMinorAndFlags()
    {
        var issue = MakeIssue(Severity.Critical, citationLowConfidenceFlags: true);

        var result = ConfidenceGuard.Apply([issue]).Single();

        Assert.Equal(Severity.Minor, result.Severity);
        Assert.True(result.IsLowConfidenceInput);
    }

    [Fact]
    public void CriticalWithAllHighConfidenceCitations_StaysCritical()
    {
        var issue = MakeIssue(Severity.Critical, false, false);

        var result = ConfidenceGuard.Apply([issue]).Single();

        Assert.Equal(Severity.Critical, result.Severity);
        Assert.False(result.IsLowConfidenceInput);
    }

    [Fact]
    public void CriticalWithAnyLowConfidenceCitation_DowngradesEvenIfOthersAreHigh()
    {
        var issue = MakeIssue(Severity.Critical, false, true, false);

        var result = ConfidenceGuard.Apply([issue]).Single();

        Assert.Equal(Severity.Minor, result.Severity);
        Assert.True(result.IsLowConfidenceInput);
    }

    [Fact]
    public void MajorAndMinor_UnchangedRegardlessOfConfidence()
    {
        // Only Critical is gated. The threshold is "this signal is too noisy
        // to block a packet"; that question doesn't apply to Major / Minor.
        var major = MakeIssue(Severity.Major, citationLowConfidenceFlags: true);
        var minor = MakeIssue(Severity.Minor, citationLowConfidenceFlags: true);

        var result = ConfidenceGuard.Apply([major, minor]);

        Assert.Equal(Severity.Major, result[0].Severity);
        Assert.Equal(Severity.Minor, result[1].Severity);
        Assert.False(result[0].IsLowConfidenceInput);
        Assert.False(result[1].IsLowConfidenceInput);
    }

    [Fact]
    public void Idempotent_SecondPassIsNoOp()
    {
        // The downgraded Issue is Minor, which doesn't match the Critical
        // precondition; running the guard again over the result must leave
        // it untouched.
        var issue = MakeIssue(Severity.Critical, citationLowConfidenceFlags: true);

        var first = ConfidenceGuard.Apply([issue]);
        var second = ConfidenceGuard.Apply(first);

        Assert.Equal(Severity.Minor, second[0].Severity);
        Assert.True(second[0].IsLowConfidenceInput);
    }

    [Fact]
    public void CriticalWithNoCitations_StaysCritical()
    {
        // A no-citation Critical (e.g. sanctions-missing) can't have a
        // low-confidence input — there's no input. The guard leaves it alone.
        var issue = new Issue("test", Severity.Critical, "msg", "rem", Array.Empty<Citation>());

        var result = ConfidenceGuard.Apply([issue]).Single();

        Assert.Equal(Severity.Critical, result.Severity);
        Assert.False(result.IsLowConfidenceInput);
    }

    [Fact]
    public void MultiSourceLLMIssue_OneWeakCitation_DowngradesWholeIssue()
    {
        // Realistic NPI-taxonomy-match / identity-coherence shape: an Issue
        // composed of citations from multiple source documents, only one of
        // which is low-confidence. The guard's "any low-confidence" policy
        // downgrades the whole Issue — pin the behavior deliberately so a
        // future "all low-confidence" or "majority low-confidence" tweak
        // shows up as a test failure rather than a silent score shift.
        var issue = new Issue(
            "npi_taxonomy_match",
            Severity.Critical,
            "License taxonomy maps to X, boardCert states Y.",
            "Confirm specialty.",
            new[]
            {
                new Citation("npi_taxonomy_match", "207RC0000X") { LowConfidence = false },
                new Citation("npi_taxonomy_match", "Cardiology") { LowConfidence = true },
            });

        var result = ConfidenceGuard.Apply([issue]).Single();

        Assert.Equal(Severity.Minor, result.Severity);
        Assert.True(result.IsLowConfidenceInput);
    }
}
