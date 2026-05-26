using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using PacketReady.Application.Intake.Commands.StartIntake;
using PacketReady.Application.Intake.Exceptions;
using PacketReady.Application.Intake.MagicLinks;
using PacketReady.Application.Providers.Exceptions;
using PacketReady.Domain.Intake;
using PacketReady.Domain.MagicLinks;
using PacketReady.Domain.Providers;
using PacketReady.Infrastructure.Audit;
using PacketReady.Infrastructure.Persistence;
using PacketReady.Tests.Infrastructure;
using Xunit;

namespace PacketReady.Tests.Application.Intake.Commands.StartIntake;

public class StartIntakeCommandHandlerTests : IDisposable
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryContextFactory _factory;
    private readonly PacketReadyDbContext _db;
    private readonly FakeTimeProvider _clock;
    private readonly Mock<IMagicLinkAuthority> _authority;

    public StartIntakeCommandHandlerTests()
    {
        _factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        _db = _factory.CreateDbContext();
        _clock = new FakeTimeProvider(T0);

        _authority = new Mock<IMagicLinkAuthority>(MockBehavior.Strict);
        _authority
            .Setup(a => a.SignToken(It.IsAny<MagicLink>()))
            .Returns<MagicLink>(l => $"signed-token-for-{l.Id:D}");
    }

    public void Dispose() => _db.Dispose();

    private StartIntakeCommandHandler Build()
    {
        var audit = new AuditWriter(_db, _factory, NullLogger<AuditWriter>.Instance);
        return new StartIntakeCommandHandler(
            _db,
            _authority.Object,
            audit,
            _clock,
            NullLogger<StartIntakeCommandHandler>.Instance);
    }

    private async Task<Provider> SeedProviderAsync()
    {
        // Minimum legitimate profile for Provider.Create (P3 shape) — the
        // intake flow only cares about the provider's existence; the
        // profile contents are downstream.
        var profile = ProviderProfile.Create(
            fullName: "Henry Anderson",
            dateOfBirth: new DateOnly(1980, 1, 15),
            npi: "1234567890",
            credentialingState: "CA",
            nowUtc: T0);
        var provider = Provider.Create(profile, T0);
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();
        return provider;
    }

    // ───────────────────────────────────────────────── happy path ────────

    [Fact]
    public async Task Handle_CreatesSessionAndLinkAndReturnsSignedToken()
    {
        var provider = await SeedProviderAsync();
        var handler = Build();

        var result = await handler.Handle(
            new StartIntakeCommand(provider.Id),
            CancellationToken.None);

        Assert.Equal(provider.Id, result.ProviderId);
        Assert.NotEqual(Guid.Empty, result.IntakeSessionId);
        Assert.NotEqual(Guid.Empty, result.MagicLinkId);
        Assert.Equal($"signed-token-for-{result.MagicLinkId:D}", result.Token);
        Assert.Equal(T0 + MagicLink.DefaultTtl, result.ExpiresAt);

        var session = await _db.IntakeSessions.FindAsync(result.IntakeSessionId);
        Assert.NotNull(session);
        Assert.Equal(IntakeState.Pending, session!.State);
        Assert.Equal(IntakeSession.DefaultTurnBudget, session.TurnBudget);
        Assert.Equal(0, session.TurnsConsumed);

        var link = await _db.MagicLinks.FindAsync(result.MagicLinkId);
        Assert.NotNull(link);
        Assert.Equal(provider.Id, link!.ProviderId);
        Assert.Equal(T0, link.IssuedAt);
        Assert.Null(link.ConsumedAt);
    }

    [Fact]
    public async Task Handle_StagesIntakeStartedAuditRow()
    {
        var provider = await SeedProviderAsync();
        var handler = Build();

        var result = await handler.Handle(
            new StartIntakeCommand(provider.Id),
            CancellationToken.None);

        var audit = Assert.Single(_db.AuditEvents.ToList(),
            e => e.EventType == "IntakeStarted");
        Assert.Equal(provider.Id, audit.ProviderId);
        Assert.Contains(result.IntakeSessionId.ToString(), audit.Payload);
        Assert.Contains(result.MagicLinkId.ToString(), audit.Payload);
        // The signed token is NOT in the audit payload — by design (signing
        // is reproducible from id + secret, and audit rows should not be
        // replayable to bypass the signature check).
        Assert.DoesNotContain(result.Token, audit.Payload);
    }

    [Fact]
    public async Task Handle_SignsTokenAfterStagingTheLink()
    {
        // Behavioral assertion: the link's Id must be populated before
        // SignToken is called. Strict mock + Returns<MagicLink> captures
        // this because the lambda runs at the call site.
        Guid? observedId = null;
        _authority
            .Setup(a => a.SignToken(It.IsAny<MagicLink>()))
            .Callback<MagicLink>(l => observedId = l.Id)
            .Returns<MagicLink>(l => "test-token");

        var provider = await SeedProviderAsync();
        var handler = Build();

        var result = await handler.Handle(
            new StartIntakeCommand(provider.Id),
            CancellationToken.None);

        Assert.NotNull(observedId);
        Assert.Equal(result.MagicLinkId, observedId);
    }

    // ─────────────────────────────────────────────── error paths ────────

    [Fact]
    public async Task Handle_RejectsEmptyProviderId()
    {
        var handler = Build();
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => handler.Handle(new StartIntakeCommand(Guid.Empty), CancellationToken.None));
        Assert.Equal("request", ex.ParamName);
    }

    [Fact]
    public async Task Handle_ProviderNotFoundThrowsTyped()
    {
        var handler = Build();
        await Assert.ThrowsAsync<ProviderNotFoundException>(
            () => handler.Handle(
                new StartIntakeCommand(Guid.NewGuid()),
                CancellationToken.None));
    }

    [Fact]
    public async Task Handle_DoubleStartThrowsIntakeAlreadyExists()
    {
        var provider = await SeedProviderAsync();
        var handler = Build();

        // First start succeeds.
        await handler.Handle(
            new StartIntakeCommand(provider.Id),
            CancellationToken.None);

        // Second start refused.
        var ex = await Assert.ThrowsAsync<IntakeAlreadyExistsException>(
            () => handler.Handle(
                new StartIntakeCommand(provider.Id),
                CancellationToken.None));
        Assert.Equal(provider.Id, ex.ProviderId);
    }
}
