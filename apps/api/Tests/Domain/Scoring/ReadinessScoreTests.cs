using PacketReady.Domain.Scoring;
using Xunit;

namespace PacketReady.Tests.Domain.Scoring;

public class ReadinessScoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProviderId = Guid.NewGuid();

    private static Issue MakeIssue(Severity severity, string validator = "TestValidator") =>
        new(
            Validator: validator,
            Severity: severity,
            Message: "msg",
            Remediation: "fix it",
            Citations: new[] { new Citation(validator, "value") });

    [Theory]
    [InlineData(100, Tier.Green)]
    [InlineData(90, Tier.Green)]
    [InlineData(85, Tier.Green)]     // lower boundary of Green
    [InlineData(84, Tier.Yellow)]    // upper boundary of Yellow
    [InlineData(70, Tier.Yellow)]
    [InlineData(60, Tier.Yellow)]    // lower boundary of Yellow
    [InlineData(59, Tier.Red)]       // upper boundary of Red
    [InlineData(30, Tier.Red)]
    [InlineData(0, Tier.Red)]
    public void Create_DerivesTierFromScore(int score, Tier expected)
    {
        var rs = ReadinessScore.Create(ProviderId, score, Array.Empty<Issue>(), Now);
        Assert.Equal(expected, rs.Tier);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Create_RejectsScoreOutOfRange(int badScore)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReadinessScore.Create(ProviderId, badScore, Array.Empty<Issue>(), Now));
        Assert.Equal("score", ex.ParamName);
    }

    [Fact]
    public void Create_RejectsNullIssues()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            ReadinessScore.Create(ProviderId, 80, null!, Now));
        Assert.Equal("issues", ex.ParamName);
    }

    [Fact]
    public void Create_CountsIssuesBySeverity()
    {
        var issues = new[]
        {
            MakeIssue(Severity.Critical),
            MakeIssue(Severity.Critical),
            MakeIssue(Severity.Major),
            MakeIssue(Severity.Minor),
            MakeIssue(Severity.Minor),
            MakeIssue(Severity.Minor),
        };

        var rs = ReadinessScore.Create(ProviderId, 50, issues, Now);

        Assert.Equal(2, rs.CriticalCount);
        Assert.Equal(1, rs.MajorCount);
        Assert.Equal(3, rs.MinorCount);
    }

    [Fact]
    public void Create_PopulatesIdAndComputedAt()
    {
        var rs = ReadinessScore.Create(ProviderId, 80, Array.Empty<Issue>(), Now);

        Assert.NotEqual(Guid.Empty, rs.Id);
        Assert.Equal(ProviderId, rs.ProviderId);
        Assert.Equal(Now, rs.ComputedAt);
    }

    [Fact]
    public void Create_RoundTripsIssuesAsJson()
    {
        var issues = new[]
        {
            MakeIssue(Severity.Critical, "LicenseValidator"),
            MakeIssue(Severity.Major, "DeaValidator"),
        };

        var rs = ReadinessScore.Create(ProviderId, 50, issues, Now);
        var deserialized = rs.GetIssues();

        Assert.Equal(2, deserialized.Count);
        Assert.Equal("LicenseValidator", deserialized[0].Validator);
        Assert.Equal(Severity.Critical, deserialized[0].Severity);
        Assert.Equal("DeaValidator", deserialized[1].Validator);
    }

    [Fact]
    public void Create_SerializesSeverityAsString()
    {
        // Proves DomainJson.Options (JsonStringEnumConverter) is applied to the
        // IssuesJson path. If a future refactor accidentally swaps in default
        // JsonSerializerOptions, this test fails — and the persisted shape that
        // ordinal-pinning was meant to defend against silently changes.
        var issues = new[] { MakeIssue(Severity.Critical) };

        var rs = ReadinessScore.Create(ProviderId, 50, issues, Now);

        Assert.Contains("\"severity\":\"Critical\"", rs.IssuesJson);
        Assert.DoesNotContain("\"severity\":3", rs.IssuesJson);
    }

    [Fact]
    public void Create_HandlesEmptyIssueList()
    {
        var rs = ReadinessScore.Create(ProviderId, 100, Array.Empty<Issue>(), Now);

        Assert.Equal(0, rs.CriticalCount);
        Assert.Equal(0, rs.MajorCount);
        Assert.Equal(0, rs.MinorCount);
        Assert.Empty(rs.GetIssues());
    }
}
