using Microsoft.EntityFrameworkCore;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;
using Xunit;

namespace PacketReady.Tests.Infrastructure;

public class ProviderPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);

    private static ProviderProfile MakeProfile() =>
        ProviderProfile.Create(
            fullName: "Dr. Jane Smith",
            dateOfBirth: new DateOnly(1980, 5, 15),
            npi: "1234567890",
            credentialingState: "CA",
            nowUtc: Now,
            license: new LicenseInfo("L1", "CA",
                new DateOnly(2020, 1, 1), new DateOnly(2030, 1, 1), LicenseStatus.Active));

    [Fact]
    public async Task Provider_RoundTripsThroughDbContext()
    {
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var provider = Provider.Create(MakeProfile(), Now);

        await using (var writer = factory.CreateDbContext())
        {
            writer.Providers.Add(provider);
            await writer.SaveChangesAsync();
        }

        await using var reader = factory.CreateDbContext();
        var loaded = await reader.Providers.SingleAsync(p => p.Id == provider.Id);

        Assert.Equal(provider.Id, loaded.Id);
        Assert.Equal(provider.CreatedAt, loaded.CreatedAt);

        // GetProfile() exercises the cache path on the rehydrated entity.
        var profile = loaded.GetProfile();
        Assert.Equal("Dr. Jane Smith", profile.FullName);
        Assert.Equal("1234567890", profile.Npi);
        Assert.Equal(LicenseStatus.Active, profile.License?.Status);
    }

    [Fact]
    public async Task ReadinessScore_RoundTripsThroughDbContext()
    {
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var provider = Provider.Create(MakeProfile(), Now);
        var issues = new[]
        {
            new Issue("LicenseValidator", Severity.Critical, "expired", "renew",
                new[] { new Citation("LicenseValidator", "2020-01-01") }),
            new Issue("DeaValidator", Severity.Major, "missing", "obtain",
                Array.Empty<Citation>()),
        };
        var score = ReadinessScore.Create(provider.Id, 75, issues, Now);

        await using (var writer = factory.CreateDbContext())
        {
            writer.Providers.Add(provider);
            writer.ReadinessScores.Add(score);
            await writer.SaveChangesAsync();
        }

        await using var reader = factory.CreateDbContext();
        var loaded = await reader.ReadinessScores.SingleAsync(s => s.Id == score.Id);

        Assert.Equal(75, loaded.Score);
        Assert.Equal(Tier.Yellow, loaded.Tier);
        Assert.Equal(1, loaded.CriticalCount);
        Assert.Equal(1, loaded.MajorCount);
        Assert.Equal(0, loaded.MinorCount);

        var deserializedIssues = loaded.GetIssues();
        Assert.Equal(2, deserializedIssues.Count);
        Assert.Equal(Severity.Critical, deserializedIssues[0].Severity);
    }

    [Fact]
    public async Task GetProfile_ReflectsFreshJsonAfterEfRehydration()
    {
        // Two contexts share an in-memory DB. Writer saves with NPI "1234567890";
        // reader loads and asserts NPI matches. If the property setter failed to
        // invalidate the cache on EF materialization, we'd get a stale value.
        // Production equivalent of ProviderTests.ProfileJson_SetterInvalidatesCache.
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var provider = Provider.Create(MakeProfile(), Now);

        await using (var writer = factory.CreateDbContext())
        {
            writer.Providers.Add(provider);
            await writer.SaveChangesAsync();
        }

        await using var reader = factory.CreateDbContext();
        var loaded = await reader.Providers.SingleAsync(p => p.Id == provider.Id);

        var first = loaded.GetProfile();
        Assert.Equal("1234567890", first.Npi);

        var second = loaded.GetProfile();
        Assert.Same(first, second);
    }

    [Fact(Skip = "InMemory provider does not enforce FK cascades; constraint provable from migration file.")]
    public Task ReadinessScore_DeletedWhenProviderDeleted() => Task.CompletedTask;

    [Fact(Skip = "InMemory provider does not emulate HasConversion<string>; covered by ReadinessScoreConfiguration and migration column type.")]
    public Task TierColumn_PersistsAsString() => Task.CompletedTask;
}
