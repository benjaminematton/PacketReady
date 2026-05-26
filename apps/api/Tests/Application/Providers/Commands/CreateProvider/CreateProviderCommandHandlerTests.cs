using Microsoft.Extensions.Time.Testing;
using PacketReady.Application.Payers;
using PacketReady.Application.Providers.Commands.CreateProvider;
using PacketReady.Domain.Providers;
using PacketReady.Tests.Infrastructure;
using Xunit;
using static PacketReady.Tests.Application.Scoring.Validators.TestProfiles;

namespace PacketReady.Tests.Application.Providers.Commands.CreateProvider;

public sealed class CreateProviderCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    private static CreateProviderCommandHandler Build(InMemoryContextFactory factory) =>
        new(
            db: factory.CreateDbContext(),
            payers: MakePayers(),
            clock: new FakeTimeProvider(Now));

    [Fact]
    public async Task NullPayerId_NullIdentity_UsesDefaults()
    {
        // Pure-placeholder path. The eval orchestrator won't take this
        // route (it always passes identity), but P5 admin-intake will
        // — the empty Provider exists before any data arrives.
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var handler = Build(factory);

        var id = await handler.Handle(new CreateProviderCommand(null, null), default);

        Assert.NotEqual(Guid.Empty, id);
        await using var read = factory.CreateDbContext();
        var saved = await read.Providers.FindAsync([id], default);
        Assert.NotNull(saved);
        Assert.Equal(Provider.DefaultPayerId, saved!.PayerId);
        var profile = saved.GetProfile();
        Assert.Equal(ProviderIdentityValidator.Placeholder.Npi, profile.Npi);
        Assert.Equal(ProviderIdentityValidator.Placeholder.CredentialingState, profile.CredentialingState);
    }

    [Fact]
    public async Task ExplicitIdentity_PersistsAsProvided()
    {
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var handler = Build(factory);
        var identity = new ProviderIdentityDto(
            FullName: "Henry Anderson, MD",
            Npi: "1234567893",
            DateOfBirth: new DateOnly(1980, 4, 15),
            CredentialingState: "NY");

        var id = await handler.Handle(
            new CreateProviderCommand("payer-b-state-medicaid", identity), default);

        await using var read = factory.CreateDbContext();
        var saved = await read.Providers.FindAsync([id], default);
        Assert.NotNull(saved);
        Assert.Equal("payer-b-state-medicaid", saved!.PayerId);
        var profile = saved.GetProfile();
        Assert.Equal("Henry Anderson, MD", profile.FullName);
        Assert.Equal("1234567893", profile.Npi);
        Assert.Equal(new DateOnly(1980, 4, 15), profile.DateOfBirth);
        Assert.Equal("NY", profile.CredentialingState);
    }

    [Fact]
    public async Task UnknownPayerId_ThrowsPayerNotConfigured()
    {
        // The endpoint catches this and maps to 422. Handler must
        // propagate the typed exception, not swallow to a 500.
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var handler = Build(factory);

        await Assert.ThrowsAsync<PayerNotConfiguredException>(() =>
            handler.Handle(new CreateProviderCommand("payer-zzz-no-yaml", null), default));

        // The provider row must not have been written.
        await using var read = factory.CreateDbContext();
        Assert.Empty(read.Providers);
    }

    [Fact]
    public async Task ExplicitIntakeBudgetTurns_PersistsOnProvider()
    {
        // The wire/endpoint path passes IntakeBudgetTurns through the
        // command to Provider.Create. The endpoint rejects <= 0 with a
        // 400; Provider.Create has the same floor at the domain
        // boundary. This test pins the happy path: a positive value
        // lands on the row.
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var handler = Build(factory);

        var id = await handler.Handle(
            new CreateProviderCommand(PayerId: null, Identity: null, IntakeBudgetTurns: 12),
            default);

        await using var read = factory.CreateDbContext();
        var saved = await read.Providers.FindAsync([id], default);
        Assert.NotNull(saved);
        Assert.Equal(12, saved!.IntakeBudgetTurns);
    }

    [Fact]
    public async Task NonPositiveIntakeBudgetTurns_PropagatesAsArgumentOutOfRange()
    {
        // Endpoint rejects with 400 (see ProviderEndpoints); a
        // mediator-only caller (in-process test, future internal job)
        // still hits the domain floor via Provider.Create.
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var handler = Build(factory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            handler.Handle(
                new CreateProviderCommand(PayerId: null, Identity: null, IntakeBudgetTurns: 0),
                default));

        await using var read = factory.CreateDbContext();
        Assert.Empty(read.Providers);
    }

    [Fact]
    public async Task BlankPayerId_PropagatesAsArgumentException()
    {
        // Whitespace-only PayerId is operator bug shape. The endpoint
        // pre-validates and returns 400, so HTTP callers never reach the
        // handler with this input. A mediator-only caller (in-process
        // test, future internal job) does reach the handler — the handler
        // passes the value through to Provider.Create, which fails-loud
        // via ArgumentException rather than silently coercing to
        // DefaultPayerId (the domain and handler agree on the contract).
        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var handler = Build(factory);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.Handle(new CreateProviderCommand("   ", null), default));

        await using var read = factory.CreateDbContext();
        Assert.Empty(read.Providers);
    }
}
