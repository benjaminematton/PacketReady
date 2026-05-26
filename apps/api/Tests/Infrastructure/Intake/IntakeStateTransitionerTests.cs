using Moq;
using PacketReady.Application.Intake.Agent;
using PacketReady.Application.Intake.MagicLinks;
using PacketReady.Domain.Intake;
using PacketReady.Domain.MagicLinks;
using PacketReady.Domain.Messaging;
using PacketReady.Domain.Providers;
using PacketReady.Infrastructure.Intake;
using PacketReady.Infrastructure.Persistence;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Intake;

public class IntakeStateTransitionerTests : IDisposable
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid ProviderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string ToAddr = "provider@example.com";

    private readonly InMemoryContextFactory _factory;
    private readonly PacketReadyDbContext _db;
    private readonly Mock<IMagicLinkAuthority> _authority;
    private readonly IntakeStateTransitioner _transitioner;

    public IntakeStateTransitionerTests()
    {
        _factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        _db = _factory.CreateDbContext();
        _authority = new Mock<IMagicLinkAuthority>();
        _authority
            .Setup(a => a.SignToken(It.IsAny<MagicLink>()))
            .Returns<MagicLink>(l => $"token-for-{l.Id:N}");
        _transitioner = new IntakeStateTransitioner(_db, _authority.Object);
    }

    public void Dispose() => _db.Dispose();

    private IntakeSession SessionInAgentProcessing(out Guid turnId)
    {
        var session = IntakeSession.Start(ProviderId, Provider.DefaultIntakeBudgetTurns, T0);
        session.NotifyInvitationSent(Guid.NewGuid(), T0.AddSeconds(1));
        turnId = Guid.NewGuid();
        session.BeginAgentTurn(turnId, T0.AddSeconds(2));
        return session;
    }

    // ───────────────────────────────────────────── terminal path ─────────

    [Fact]
    public async Task Terminal_TransitionsToComplete_NoLinkOrOutbox()
    {
        var session = SessionInAgentProcessing(out var turnId);
        var scoreId = Guid.NewGuid();
        var result = new AgentTurnResult(
            TurnId: turnId,
            IsTerminal: true,
            CompletedReadinessScoreId: scoreId,
            ProposedFollowupSubject: null,
            ProposedFollowupBody: null,
            StepsConsumed: 3,
            InputTokensConsumed: 100,
            OutputTokensConsumed: 50,
            WallClockConsumed: TimeSpan.FromSeconds(5));

        var effect = _transitioner.Apply(session, result, ToAddr, T0.AddSeconds(10));
        await _db.SaveChangesAsync();

        Assert.Equal(IntakeState.Complete, session.State);
        var payload = Assert.IsType<ProviderState.Complete>(session.GetState());
        Assert.Equal(scoreId, payload.ReadinessScoreId);

        Assert.Null(effect.NewMagicLinkId);
        Assert.Null(effect.QueuedOutboundMessageId);
        Assert.Empty(_db.MagicLinks);
        Assert.Empty(_db.OutboundMessages);
    }

    [Fact]
    public void Terminal_WithoutScoreId_Throws()
    {
        var session = SessionInAgentProcessing(out var turnId);
        var result = new AgentTurnResult(
            TurnId: turnId,
            IsTerminal: true,
            CompletedReadinessScoreId: null,  // contradictory shape
            ProposedFollowupSubject: null,
            ProposedFollowupBody: null,
            StepsConsumed: 1, InputTokensConsumed: 0, OutputTokensConsumed: 0,
            WallClockConsumed: TimeSpan.Zero);

        Assert.Throws<InvalidOperationException>(
            () => _transitioner.Apply(session, result, ToAddr, T0.AddSeconds(10)));
    }

    // ───────────────────────────────────────── followup path ─────────────

    [Fact]
    public async Task Followup_IssuesNewLinkAndQueuesOutbox()
    {
        var session = SessionInAgentProcessing(out var turnId);
        var result = new AgentTurnResult(
            TurnId: turnId,
            IsTerminal: false,
            CompletedReadinessScoreId: null,
            ProposedFollowupSubject: "PacketReady — one more item",
            ProposedFollowupBody: "Hi, please upload your DEA.",
            StepsConsumed: 4,
            InputTokensConsumed: 200,
            OutputTokensConsumed: 80,
            WallClockConsumed: TimeSpan.FromSeconds(10));

        var effect = _transitioner.Apply(session, result, ToAddr, T0.AddSeconds(20));
        await _db.SaveChangesAsync();

        // Session walked back to AwaitingProvider with the new link id.
        Assert.Equal(IntakeState.AwaitingProvider, session.State);
        var payload = Assert.IsType<ProviderState.AwaitingProvider>(session.GetState());
        Assert.NotNull(effect.NewMagicLinkId);
        Assert.Equal(effect.NewMagicLinkId, payload.MagicLinkId);

        // A magic link row was staged + saved.
        var link = Assert.Single(_db.MagicLinks);
        Assert.Equal(ProviderId, link.ProviderId);
        Assert.Equal(effect.NewMagicLinkToken, $"token-for-{link.Id:N}");

        // An outbound message was queued with the followup content.
        var outbound = Assert.Single(_db.OutboundMessages);
        Assert.Equal(MessageKind.Followup, outbound.Kind);
        Assert.Equal(ToAddr, outbound.ToAddress);
        Assert.Equal("PacketReady — one more item", outbound.Subject);
        Assert.Equal("Hi, please upload your DEA.", outbound.Body);
        Assert.Equal(OutboundMessageStatus.Queued, outbound.Status);
        Assert.Equal(effect.QueuedOutboundMessageId, outbound.Id);
    }

    [Fact]
    public void Followup_BlankToAddress_Throws()
    {
        var session = SessionInAgentProcessing(out var turnId);
        var result = new AgentTurnResult(
            TurnId: turnId,
            IsTerminal: false,
            CompletedReadinessScoreId: null,
            ProposedFollowupSubject: "subj",
            ProposedFollowupBody: "body",
            StepsConsumed: 1, InputTokensConsumed: 0, OutputTokensConsumed: 0,
            WallClockConsumed: TimeSpan.Zero);

        var ex = Assert.Throws<ArgumentException>(
            () => _transitioner.Apply(session, result, "", T0.AddSeconds(20)));
        Assert.Equal("toAddress", ex.ParamName);
    }

    // ───────────────────────────────────────── empty-turn path ───────────

    [Fact]
    public void EmptyTurn_Escalates()
    {
        var session = SessionInAgentProcessing(out var turnId);
        var result = new AgentTurnResult(
            TurnId: turnId,
            IsTerminal: false,
            CompletedReadinessScoreId: null,
            ProposedFollowupSubject: null,
            ProposedFollowupBody: null,
            StepsConsumed: 5, InputTokensConsumed: 100, OutputTokensConsumed: 25,
            WallClockConsumed: TimeSpan.FromSeconds(8));

        var effect = _transitioner.Apply(session, result, ToAddr, T0.AddSeconds(15));

        Assert.Equal(IntakeState.Escalated, session.State);
        var payload = Assert.IsType<ProviderState.Escalated>(session.GetState());
        Assert.Equal("agent-empty-turn", payload.Reason);
        Assert.Null(effect.NewMagicLinkId);
        Assert.Null(effect.QueuedOutboundMessageId);
    }
}
