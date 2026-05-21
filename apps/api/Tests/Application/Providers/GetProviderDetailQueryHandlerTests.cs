using PacketReady.Application.Providers.Queries.GetProviderDetail;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;
using PacketReady.Tests.Infrastructure;
using Xunit;

namespace PacketReady.Tests.Application.Providers;

public sealed class GetProviderDetailQueryHandlerTests
{
    private static readonly DateTimeOffset Base = new(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);

    private static ProviderProfile MakeProfile() =>
        ProviderProfile.Create(
            fullName: "Dr. Jane Smith",
            dateOfBirth: new DateOnly(1980, 5, 15),
            npi: "1234567890",
            credentialingState: "CA",
            nowUtc: Base);

    [Fact]
    public async Task UnknownProvider_ReturnsNull()
    {
        // Endpoint maps null → 404; this asserts the handler stays HTTP-agnostic.
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        await using var db = factory.CreateDbContext();
        var handler = new GetProviderDetailQueryHandler(db);

        var result = await handler.Handle(
            new GetProviderDetailQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ProviderWithoutScore_ReturnsDtoWithNullLatestScore()
    {
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var provider = Provider.Create(MakeProfile(), Base);

        await using (var writer = factory.CreateDbContext())
        {
            writer.Providers.Add(provider);
            await writer.SaveChangesAsync();
        }

        await using var reader = factory.CreateDbContext();
        var dto = await new GetProviderDetailQueryHandler(reader)
            .Handle(new GetProviderDetailQuery(provider.Id), CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(provider.Id, dto!.Id);
        Assert.Equal("Dr. Jane Smith", dto.FullName);
        Assert.Equal("1234567890", dto.Npi);
        Assert.Equal("CA", dto.CredentialingState);
        Assert.Equal(provider.CreatedAt, dto.CreatedAt);
        Assert.Null(dto.LatestScore);
    }

    [Fact]
    public async Task ProviderWithScore_ReturnsFullLatestScoreIncludingIssues()
    {
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var provider = Provider.Create(MakeProfile(), Base);
        var issues = new[]
        {
            new Issue("LicenseValidator", Severity.Critical, "expired", "renew now",
                new[] { new Citation("LicenseValidator", "2020-01-01") }),
            new Issue("DeaValidator", Severity.Major, "missing", "obtain DEA",
                Array.Empty<Citation>()),
        };
        var older  = ReadinessScore.Create(provider.Id, 50, Array.Empty<Issue>(), Base.AddMinutes(-30));
        var latest = ReadinessScore.Create(provider.Id, 75, issues,                Base.AddMinutes(-1));

        await using (var writer = factory.CreateDbContext())
        {
            writer.Providers.Add(provider);
            writer.ReadinessScores.AddRange(older, latest);
            await writer.SaveChangesAsync();
        }

        await using var reader = factory.CreateDbContext();
        var dto = await new GetProviderDetailQueryHandler(reader)
            .Handle(new GetProviderDetailQuery(provider.Id), CancellationToken.None);

        Assert.NotNull(dto);
        Assert.NotNull(dto!.LatestScore);
        Assert.Equal(latest.Id, dto.LatestScore!.Id);
        Assert.Equal(75, dto.LatestScore.Score);
        Assert.Equal(Tier.Yellow, dto.LatestScore.Tier);
        Assert.Equal(1, dto.LatestScore.CriticalCount);
        Assert.Equal(1, dto.LatestScore.MajorCount);
        Assert.Equal(0, dto.LatestScore.MinorCount);

        // Side-panel needs the full Issue payload, not just counts.
        Assert.Equal(2, dto.LatestScore.Issues.Count);
        Assert.Contains(dto.LatestScore.Issues,
            i => i.Validator == "LicenseValidator" && i.Severity == Severity.Critical);
    }

    [Fact]
    public async Task SameTickScores_TiebreakerOnIdDeterministic()
    {
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var provider = Provider.Create(MakeProfile(), Base);

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
        var dto = await new GetProviderDetailQueryHandler(reader)
            .Handle(new GetProviderDetailQuery(provider.Id), CancellationToken.None);

        Assert.Equal(expectedWinner.Id, dto!.LatestScore!.Id);
    }
}
