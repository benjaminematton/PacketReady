using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using PacketReady.Application.Intake.Agent;
using PacketReady.Application.Intake.MagicLinks;
using PacketReady.Domain.Intake;
using PacketReady.Domain.MagicLinks;
using PacketReady.Domain.Messaging;
using PacketReady.Domain.Providers;
using PacketReady.Infrastructure.Audit;
using PacketReady.Infrastructure.Intake;
using PacketReady.Infrastructure.Persistence;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Intake;

public class IntakeTurnJobTests : IDisposable
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProviderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string ToAddr = "provider@example.com";

    private readonly InMemoryContextFactory _factory;
    private readonly PacketReadyDbContext _db;
    private readonly FakeTimeProvider _clock;
    private readonly Mock<IIntakeAgent> _agent;
    private readonly Mock<IMagicLinkAuthority> _authority;
    private readonly IntakeTurnJob _job;

    public IntakeTurnJobTests()
    {
        _factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        _db = _factory.CreateDbContext();
        _clock = new FakeTimeProvider(T0);
        _agent = new Mock<IIntakeAgent>(MockBehavior.Strict);
        _authority = new Mock<IMagicLinkAuthority>();
        _authority
            .Setup(a => a.SignToken(It.IsAny<MagicLink>()))
            .Returns<MagicLink>(l => $"token-for-{l.Id:N}");

        var audit = new AuditWriter(_db, _factory, NullLogger<AuditWriter>.Instance);
        var transitioner = new IntakeStateTransitioner(_db, _authority.Object);

        _job = new IntakeTurnJob(
            _db,
            _agent.Object,
            transitioner,
            audit,
            _clock,
            NullLogger<IntakeTurnJob>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedSessionAwaitingProviderAsync(int turnBudget = 8)
    {
        var profile = ProviderProfile.Create(
            fullName: "Henry Anderson",
            dateOfBirth: new DateOnly(1980, 1, 15),
            npi: "1234567890",
            credentialingState: "CA",
            nowUtc: T0);
        var provider = Provider.Create(profile, T0);
        // Force the seeded provider to use our well-known id so the test
        // helper's ProviderId constant matches the row.
        typeof(Provider).GetProperty(nameof(Provider.Id))!.SetValue(provider, ProviderId);
        _db.Providers.Add(provider);

        var session = IntakeSession.Start(ProviderId, turnBudget, T0);
        session.NotifyInvitationSent(Guid.NewGuid(), T0.AddSeconds(1));
        _db.IntakeSessions.Add(session);

        // Seed an earlier OutboundMessage so the transitioner can find
        // the to-address.
        _db.OutboundMessages.Add(OutboundMessage.Compose(
            providerId: ProviderId,
            turnId: session.Id,
            kind: MessageKind.IntakeInvitation,
            toAddress: ToAddr,
            subject: "PacketReady — your intake",
            body: "Click here.",
            composedAt: T0.AddSeconds(-10)));

        await _db.SaveChangesAsync();
    }

    // ───────────────────────────────────────────── happy paths ──────────

    [Fact]
    public async Task RunAsync_AgentReturnsTerminal_TransitionsToComplete()
    {
        await SeedSessionAwaitingProviderAsync();
        var scoreId = Guid.NewGuid();
        _agent
            .Setup(a => a.RunTurnAsync(ProviderId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid pid, Guid tid, CancellationToken _) =>
                new AgentTurnResult(
                    TurnId: tid,
                    IsTerminal: true,
                    CompletedReadinessScoreId: scoreId,
                    ProposedFollowupSubject: null,
                    ProposedFollowupBody: null,
                    StepsConsumed: 3,
                    InputTokensConsumed: 100,
                    OutputTokensConsumed: 50,
                    WallClockConsumed: TimeSpan.FromSeconds(5)));

        await _job.RunAsync(ProviderId);

        var session = await _factory.CreateDbContext().IntakeSessions
            .SingleAsync(s => s.ProviderId == ProviderId);
        Assert.Equal(IntakeState.Complete, session.State);
        Assert.Equal(1, session.TurnsConsumed);
    }

    [Fact]
    public async Task RunAsync_AgentProposesFollowup_TransitionsToAwaitingProviderWithNewLink()
    {
        await SeedSessionAwaitingProviderAsync();
        _agent
            .Setup(a => a.RunTurnAsync(ProviderId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid pid, Guid tid, CancellationToken _) =>
                new AgentTurnResult(
                    TurnId: tid,
                    IsTerminal: false,
                    CompletedReadinessScoreId: null,
                    ProposedFollowupSubject: "PacketReady — one more item",
                    ProposedFollowupBody: "Hi, please upload your DEA.",
                    StepsConsumed: 4,
                    InputTokensConsumed: 200,
                    OutputTokensConsumed: 80,
                    WallClockConsumed: TimeSpan.FromSeconds(8)));

        await _job.RunAsync(ProviderId);

        // Fresh context to verify persisted state.
        using var verify = _factory.CreateDbContext();
        var session = await verify.IntakeSessions.SingleAsync(s => s.ProviderId == ProviderId);
        Assert.Equal(IntakeState.AwaitingProvider, session.State);
        Assert.Equal(1, session.TurnsConsumed);

        // A new magic link landed.
        var links = await verify.MagicLinks.Where(l => l.ProviderId == ProviderId).ToListAsync();
        Assert.Equal(1, links.Count);  // only the followup link — seed didn't create one

        // A followup outbox row landed alongside the seeded intake_invitation.
        var followups = await verify.OutboundMessages
            .Where(m => m.ProviderId == ProviderId && m.Kind == MessageKind.Followup)
            .ToListAsync();
        Assert.Single(followups);
        Assert.Equal(ToAddr, followups[0].ToAddress);
    }

    // ───────────────────────────────────────────── budget paths ─────────

    [Fact]
    public async Task RunAsync_PreBudgetCheck_TurnsConsumedEqualsBudget_EscalatesWithoutAgent()
    {
        // Seed with TurnsConsumed already at the budget. The pre-check
        // should escalate before invoking the agent.
        await SeedSessionAwaitingProviderAsync(turnBudget: 2);

        // Advance the seeded session on the same context that the job
        // queries — EF InMemory returns the tracked instance, so a fresh
        // context's mutations wouldn't be visible without clearing the
        // tracker.
        var session = await _db.IntakeSessions.SingleAsync(s => s.ProviderId == ProviderId);
        session.BeginAgentTurn(Guid.NewGuid(), T0.AddSeconds(2));
        session.EndAgentTurn(
            new AgentTurnOutcome { ContinueWithMagicLinkId = Guid.NewGuid() },
            T0.AddSeconds(3));
        session.BeginAgentTurn(Guid.NewGuid(), T0.AddSeconds(4));
        session.EndAgentTurn(
            new AgentTurnOutcome { ContinueWithMagicLinkId = Guid.NewGuid() },
            T0.AddSeconds(5));
        await _db.SaveChangesAsync();
        Assert.Equal(2, session.TurnsConsumed);

        await _job.RunAsync(ProviderId);

        _agent.Verify(a => a.RunTurnAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        using var verify = _factory.CreateDbContext();
        var after = await verify.IntakeSessions.SingleAsync(s => s.ProviderId == ProviderId);
        Assert.Equal(IntakeState.Escalated, after.State);
        var payload = Assert.IsType<ProviderState.Escalated>(after.GetState());
        Assert.Equal("turn-budget-exhausted", payload.Reason);
    }

    [Fact]
    public async Task RunAsync_AgentThrowsBudgetExhausted_EscalatesWithAxis()
    {
        await SeedSessionAwaitingProviderAsync();
        _agent
            .Setup(a => a.RunTurnAsync(ProviderId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BudgetExhaustedException("tokens"));

        await _job.RunAsync(ProviderId);

        using var verify = _factory.CreateDbContext();
        var session = await verify.IntakeSessions.SingleAsync(s => s.ProviderId == ProviderId);
        Assert.Equal(IntakeState.Escalated, session.State);
        var payload = Assert.IsType<ProviderState.Escalated>(session.GetState());
        Assert.Equal("budget:tokens", payload.Reason);

        // Turn was started before the agent threw, so TurnsConsumed bumped.
        Assert.Equal(1, session.TurnsConsumed);
    }

    // ───────────────────────────────────────────── edge cases ──────────

    [Fact]
    public async Task RunAsync_NoSessionForProvider_NoOps()
    {
        // No seed, no session row. Job should log + return without throwing.
        await _job.RunAsync(ProviderId);
        _agent.Verify(a => a.RunTurnAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_EmptyProviderId_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _job.RunAsync(Guid.Empty));
    }
}
