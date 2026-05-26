using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PacketReady.Application.Scoring;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain;
using PacketReady.Domain.Scoring;
using PacketReady.Seed;
using Xunit;

namespace PacketReady.Tests.Application.Scoring;

/// <summary>
/// Guards the eval rubric against silent drift. The seed CLI proves the same math
/// end-to-end against Postgres + MediatR; this theory proves it in &lt;100ms with no
/// DB by feeding the fixtures straight through the validator suite and
/// <see cref="ScoreSynthesizer"/>. If anyone retunes <c>CriticalPenalty</c>,
/// reorders the <see cref="Tier"/> thresholds, or accidentally suppresses an Issue,
/// the matching fixture breaks here long before the seed binary is invoked.
/// </summary>
public sealed class FixtureArithmeticTests
{
    // Anchor for placeholder resolution. Any UTC moment works as long as the same
    // value is fed to both the resolver and the FakeTimeProvider the validators see.
    private static readonly DateTimeOffset Now =
        new(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("provider-green.json", 100, Tier.Green)]
    [InlineData("provider-yellow.json", 62, Tier.Yellow)]
    [InlineData("provider-red.json", 34, Tier.Red)]
    public async Task Fixture_Matches_ExpectedScoreAndTier(
        string filename, int expectedScore, Tier expectedTier)
    {
        var fixture = LoadFixture(filename);

        // Sanity: the fixture's declared expectations match what the test asserts
        // here. If a fixture is edited to a new expected value, the [InlineData]
        // entry above must move in lockstep.
        Assert.Equal(expectedScore, fixture.ExpectedScore);
        Assert.Equal(expectedTier, fixture.ExpectedTier);

        var clock = new FakeTimeProvider(Now);
        var payers = Validators.TestProfiles.MakePayers();
        var validators = new IValidator[]
        {
            new LicenseStatusValidator(clock),
            new DeaStatusValidator(clock),
            new BoardCertificationValidator(clock, payers),
            new SanctionsCheckValidator(clock),
        };

        var perValidator = await Task.WhenAll(
            validators.Select(v => Validators.ValidatorTestExtensions.RunAsync(v, fixture.Profile, default)));
        var issues = perValidator.SelectMany(x => x).ToList();

        var score = ScoreSynthesizer.Compute(issues);
        var tier = TierExtensions.FromScore(score);

        Assert.Equal(expectedScore, score);
        Assert.Equal(expectedTier, tier);
    }

    private static FixtureModel LoadFixture(string filename)
    {
        var path = Path.Combine(FixturesDir(), filename);
        var raw = File.ReadAllText(path);
        var resolved = DatePlaceholderResolver.Resolve(raw, Now);
        return JsonSerializer.Deserialize<FixtureModel>(resolved, DomainJson.Options)
            ?? throw new InvalidOperationException($"Failed to parse {filename}");
    }

    /// <summary>
    /// Walks up from the test binary's output dir to the repo root, then dives into
    /// <c>evals/fixtures</c>. Avoids hard-coding a path that breaks under different
    /// runners (xUnit, dotnet test, IDE) which all set CWD differently.
    /// </summary>
    private static string FixturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PacketReady.slnx")))
        {
            // PacketReady.slnx lives at apps/api; the repo root holds tools/ and
            // evals/. Walk one more level once we hit the slnx.
            if (dir.Parent is null) break;
            dir = dir.Parent;
        }
        var root = dir?.Parent?.Parent
            ?? throw new DirectoryNotFoundException("Could not locate repo root from test binary path.");

        var fixtures = Path.Combine(root.FullName, "evals", "fixtures");
        if (!Directory.Exists(fixtures))
            throw new DirectoryNotFoundException($"Fixtures dir not found at {fixtures}.");
        return fixtures;
    }
}
