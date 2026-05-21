using PacketReady.Application.Providers.Queries.ListProviders;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;
using PacketReady.Tests.Infrastructure;
using Xunit;

namespace PacketReady.Tests.Application.Providers;

public sealed class ListProvidersQueryHandlerTests
{
    private static readonly DateTimeOffset Base = new(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);

    private static ProviderProfile MakeProfile(string name, string npi) =>
        ProviderProfile.Create(
            fullName: name,
            dateOfBirth: new DateOnly(1980, 5, 15),
            npi: npi,
            credentialingState: "CA",
            nowUtc: Base);

    [Fact]
    public async Task EmptyDb_ReturnsEmptyList()
    {
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        await using var db = factory.CreateDbContext();
        var handler = new ListProvidersQueryHandler(db);

        var result = await handler.Handle(new ListProvidersQuery(), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ProviderWithoutScore_ReturnsNullScoreFields()
    {
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var provider = Provider.Create(MakeProfile("Dr. Alice Adams", "1111111111"), Base);

        await using (var writer = factory.CreateDbContext())
        {
            writer.Providers.Add(provider);
            await writer.SaveChangesAsync();
        }

        await using var reader = factory.CreateDbContext();
        var result = await new ListProvidersQueryHandler(reader)
            .Handle(new ListProvidersQuery(), CancellationToken.None);

        var row = Assert.Single(result);
        Assert.Equal("Dr. Alice Adams", row.FullName);
        Assert.Null(row.LatestScore);
        Assert.Null(row.LatestTier);
        Assert.Null(row.LatestComputedAt);
    }

    [Fact]
    public async Task ProviderWithMultipleScores_ReturnsMostRecent()
    {
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var provider = Provider.Create(MakeProfile("Dr. Bob Brown", "2222222222"), Base);

        // Three scores; the middle ComputedAt is the chronological latest so we know
        // ordering is by ComputedAt DESC, not insertion order.
        var older  = ReadinessScore.Create(provider.Id, 50, Array.Empty<Issue>(), Base.AddMinutes(-30));
        var latest = ReadinessScore.Create(provider.Id, 92, Array.Empty<Issue>(), Base.AddMinutes(-1));
        var middle = ReadinessScore.Create(provider.Id, 70, Array.Empty<Issue>(), Base.AddMinutes(-15));

        await using (var writer = factory.CreateDbContext())
        {
            writer.Providers.Add(provider);
            writer.ReadinessScores.AddRange(older, latest, middle);
            await writer.SaveChangesAsync();
        }

        await using var reader = factory.CreateDbContext();
        var result = await new ListProvidersQueryHandler(reader)
            .Handle(new ListProvidersQuery(), CancellationToken.None);

        var row = Assert.Single(result);
        Assert.Equal(92, row.LatestScore);
        Assert.Equal(Tier.Green, row.LatestTier);
        Assert.Equal(latest.ComputedAt, row.LatestComputedAt);
    }

    [Fact]
    public async Task SameTickScores_TiebreakerOnIdDeterministic()
    {
        // Two scores with identical ComputedAt — the handler must pick deterministically.
        // ThenByDescending(Id) means the larger Guid wins. We assert which Guid is
        // larger and then confirm its score is the one returned.
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var provider = Provider.Create(MakeProfile("Dr. Carol Chen", "3333333333"), Base);

        var a = ReadinessScore.Create(provider.Id, 40, Array.Empty<Issue>(), Base);
        var b = ReadinessScore.Create(provider.Id, 80, Array.Empty<Issue>(), Base);
        var expectedWinner = a.Id.CompareTo(b.Id) > 0 ? a : b;

        await using (var writer = factory.CreateDbContext())
        {
            writer.Providers.Add(provider);
            writer.ReadinessScores.AddRange(a, b);
            await writer.SaveChangesAsync();
        }

        await using var reader = factory.CreateDbContext();
        var result = await new ListProvidersQueryHandler(reader)
            .Handle(new ListProvidersQuery(), CancellationToken.None);

        var row = Assert.Single(result);
        Assert.Equal(expectedWinner.Score, row.LatestScore);
    }

    [Fact]
    public async Task MultipleProviders_OrderedByCreatedAtAscending()
    {
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var first  = Provider.Create(MakeProfile("Dr. First",  "1000000001"), Base);
        var second = Provider.Create(MakeProfile("Dr. Second", "1000000002"), Base.AddSeconds(1));
        var third  = Provider.Create(MakeProfile("Dr. Third",  "1000000003"), Base.AddSeconds(2));

        // Insert out of order — the handler's OrderBy must override insertion order.
        await using (var writer = factory.CreateDbContext())
        {
            writer.Providers.AddRange(third, first, second);
            await writer.SaveChangesAsync();
        }

        await using var reader = factory.CreateDbContext();
        var result = await new ListProvidersQueryHandler(reader)
            .Handle(new ListProvidersQuery(), CancellationToken.None);

        Assert.Equal(
            new[] { "Dr. First", "Dr. Second", "Dr. Third" },
            result.Select(r => r.FullName).ToArray());
    }
}
