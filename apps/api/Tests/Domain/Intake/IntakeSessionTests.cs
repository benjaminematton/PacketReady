using System.Text.Json;
using PacketReady.Domain;
using PacketReady.Domain.Intake;
using Xunit;

namespace PacketReady.Tests.Domain.Intake;

public class IntakeSessionTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddMinutes(1);
    private static readonly DateTimeOffset T2 = T0.AddMinutes(2);
    private static readonly DateTimeOffset T3 = T0.AddMinutes(3);
    private static readonly DateTimeOffset T4 = T0.AddMinutes(4);

    // ───────────────────────────────────────────────────────── Start ─────

    [Fact]
    public void Start_PopulatesEverythingAndLandsInPending()
    {
        var providerId = Guid.NewGuid();

        var session = IntakeSession.Start(providerId, turnBudget: 8, nowUtc: T0);

        Assert.NotEqual(Guid.Empty, session.Id);
        Assert.Equal(providerId, session.ProviderId);
        Assert.Equal(IntakeState.Pending, session.State);
        Assert.Equal(0, session.TurnsConsumed);
        Assert.Equal(8, session.TurnBudget);
        Assert.Equal(T0, session.CreatedAt);
        Assert.Equal(T0, session.LastTransitionAt);

        var payload = Assert.IsType<ProviderState.Pending>(session.GetState());
        Assert.Equal(T0, payload.CreatedAt);
    }

    [Fact]
    public void Start_RejectsEmptyProviderId()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => IntakeSession.Start(Guid.Empty, turnBudget: 8, nowUtc: T0));
        Assert.Equal("providerId", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Start_RejectsNonPositiveTurnBudget(int budget)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => IntakeSession.Start(Guid.NewGuid(), budget, T0));
        Assert.Equal("turnBudget", ex.ParamName);
    }

    // ──────────────────────────────────────────── NotifyInvitationSent ────

    [Fact]
    public void NotifyInvitationSent_PendingToAwaitingProvider()
    {
        var session = Pending();
        var linkId = Guid.NewGuid();

        session.NotifyInvitationSent(linkId, T1);

        Assert.Equal(IntakeState.AwaitingProvider, session.State);
        Assert.Equal(T1, session.LastTransitionAt);
        Assert.Equal(0, session.TurnsConsumed);

        var payload = Assert.IsType<ProviderState.AwaitingProvider>(session.GetState());
        Assert.Equal(linkId, payload.MagicLinkId);
        Assert.Equal(0, payload.RemindersSent);
    }

    [Fact]
    public void NotifyInvitationSent_RejectsEmptyLinkId()
    {
        var session = Pending();
        var ex = Assert.Throws<ArgumentException>(
            () => session.NotifyInvitationSent(Guid.Empty, T1));
        Assert.Equal("magicLinkId", ex.ParamName);
    }

    [Fact]
    public void NotifyInvitationSent_ThrowsFromNonPending()
    {
        var session = AwaitingProvider(out _);
        Assert.Throws<InvalidOperationException>(
            () => session.NotifyInvitationSent(Guid.NewGuid(), T2));
    }

    // ─────────────────────────────────────────────── BeginAgentTurn ─────

    [Fact]
    public void BeginAgentTurn_AwaitingProviderToAgentProcessing_IncrementsTurnsConsumed()
    {
        var session = AwaitingProvider(out _);
        var turnId = Guid.NewGuid();

        session.BeginAgentTurn(turnId, T2);

        Assert.Equal(IntakeState.AgentProcessing, session.State);
        Assert.Equal(1, session.TurnsConsumed);
        Assert.Equal(T2, session.LastTransitionAt);

        var payload = Assert.IsType<ProviderState.AgentProcessing>(session.GetState());
        Assert.Equal(turnId, payload.TurnId);
        Assert.Equal(T2, payload.StartedAt);
    }

    [Fact]
    public void BeginAgentTurn_RejectsEmptyTurnId()
    {
        var session = AwaitingProvider(out _);
        var ex = Assert.Throws<ArgumentException>(
            () => session.BeginAgentTurn(Guid.Empty, T2));
        Assert.Equal("turnId", ex.ParamName);
    }

    [Fact]
    public void BeginAgentTurn_ThrowsFromPending()
    {
        var session = Pending();
        Assert.Throws<InvalidOperationException>(
            () => session.BeginAgentTurn(Guid.NewGuid(), T1));
    }

    [Fact]
    public void BeginAgentTurn_ThrowsFromAgentProcessing()
    {
        var session = AgentProcessing(out _, out _);
        Assert.Throws<InvalidOperationException>(
            () => session.BeginAgentTurn(Guid.NewGuid(), T3));
    }

    [Fact]
    public void BeginAgentTurn_ThrowsWhenBudgetExhausted()
    {
        // Walk turns 1..budget. The (budget+1)th BeginAgentTurn must throw.
        var session = IntakeSession.Start(Guid.NewGuid(), turnBudget: 2, nowUtc: T0);
        session.NotifyInvitationSent(Guid.NewGuid(), T0.AddSeconds(1));

        for (var i = 1; i <= 2; i++)
        {
            session.BeginAgentTurn(Guid.NewGuid(), T0.AddSeconds(i + 1));
            session.EndAgentTurn(
                new AgentTurnOutcome { ContinueWithMagicLinkId = Guid.NewGuid() },
                T0.AddSeconds(i + 2));
        }

        Assert.Equal(2, session.TurnsConsumed);
        var ex = Assert.Throws<InvalidOperationException>(
            () => session.BeginAgentTurn(Guid.NewGuid(), T0.AddMinutes(5)));
        Assert.Contains("budget already exhausted", ex.Message);
    }

    // ─────────────────────────────────────────────── EndAgentTurn ───────

    [Fact]
    public void EndAgentTurn_ContinueRollsBackToAwaitingProvider()
    {
        var session = AgentProcessing(out _, out _);
        var nextLinkId = Guid.NewGuid();

        session.EndAgentTurn(
            new AgentTurnOutcome { ContinueWithMagicLinkId = nextLinkId },
            T3);

        Assert.Equal(IntakeState.AwaitingProvider, session.State);
        Assert.Equal(T3, session.LastTransitionAt);

        var payload = Assert.IsType<ProviderState.AwaitingProvider>(session.GetState());
        Assert.Equal(nextLinkId, payload.MagicLinkId);
        Assert.Equal(0, payload.RemindersSent);
    }

    [Fact]
    public void EndAgentTurn_TerminalDelegatesToComplete()
    {
        var session = AgentProcessing(out _, out _);
        var scoreId = Guid.NewGuid();

        session.EndAgentTurn(
            new AgentTurnOutcome { CompletedReadinessScoreId = scoreId },
            T3);

        Assert.Equal(IntakeState.Complete, session.State);
        var payload = Assert.IsType<ProviderState.Complete>(session.GetState());
        Assert.Equal(scoreId, payload.ReadinessScoreId);
        Assert.Equal(T3, payload.CompletedAt);
    }

    [Fact]
    public void EndAgentTurn_RejectsOutcomeWithBothNull()
    {
        var session = AgentProcessing(out _, out _);
        var ex = Assert.Throws<ArgumentException>(
            () => session.EndAgentTurn(new AgentTurnOutcome(), T3));
        Assert.Equal("outcome", ex.ParamName);
    }

    [Fact]
    public void EndAgentTurn_RejectsContinueWithEmptyGuid()
    {
        var session = AgentProcessing(out _, out _);
        var ex = Assert.Throws<ArgumentException>(
            () => session.EndAgentTurn(
                new AgentTurnOutcome { ContinueWithMagicLinkId = Guid.Empty },
                T3));
        Assert.Equal("outcome", ex.ParamName);
    }

    [Fact]
    public void EndAgentTurn_ThrowsFromAwaitingProvider()
    {
        var session = AwaitingProvider(out _);
        Assert.Throws<InvalidOperationException>(
            () => session.EndAgentTurn(
                new AgentTurnOutcome { ContinueWithMagicLinkId = Guid.NewGuid() },
                T2));
    }

    // ─────────────────────────────────────────────────── Complete ───────

    [Fact]
    public void Complete_AgentProcessingToComplete()
    {
        var session = AgentProcessing(out _, out _);
        var scoreId = Guid.NewGuid();

        session.Complete(scoreId, T3);

        Assert.Equal(IntakeState.Complete, session.State);
        Assert.Equal(T3, session.LastTransitionAt);

        var payload = Assert.IsType<ProviderState.Complete>(session.GetState());
        Assert.Equal(scoreId, payload.ReadinessScoreId);
        Assert.Equal(T3, payload.CompletedAt);
    }

    [Fact]
    public void Complete_RejectsEmptyScoreId()
    {
        var session = AgentProcessing(out _, out _);
        var ex = Assert.Throws<ArgumentException>(
            () => session.Complete(Guid.Empty, T3));
        Assert.Equal("readinessScoreId", ex.ParamName);
    }

    [Fact]
    public void Complete_ThrowsFromAwaitingProvider()
    {
        var session = AwaitingProvider(out _);
        Assert.Throws<InvalidOperationException>(
            () => session.Complete(Guid.NewGuid(), T2));
    }

    // ─────────────────────────────────────────────────── Escalate ───────

    [Theory]
    [InlineData("pending")]
    [InlineData("awaiting")]
    [InlineData("processing")]
    public void Escalate_FromAnyNonTerminalStateLandsInEscalated(string startState)
    {
        var session = startState switch
        {
            "pending" => Pending(),
            "awaiting" => AwaitingProvider(out _),
            "processing" => AgentProcessing(out _, out _),
            _ => throw new InvalidOperationException(),
        };

        session.Escalate("budget:steps", T4, partialProfileJson: "{\"name\":\"jane\"}");

        Assert.Equal(IntakeState.Escalated, session.State);
        Assert.Equal(T4, session.LastTransitionAt);

        var payload = Assert.IsType<ProviderState.Escalated>(session.GetState());
        Assert.Equal("budget:steps", payload.Reason);
        Assert.Equal("{\"name\":\"jane\"}", payload.PartialProfileJson);
    }

    [Fact]
    public void Escalate_RejectsBlankReason()
    {
        var session = AwaitingProvider(out _);
        var ex = Assert.Throws<ArgumentException>(() => session.Escalate("  ", T2));
        Assert.Equal("reason", ex.ParamName);
    }

    [Fact]
    public void Escalate_DefaultsPartialProfileToEmptyObject()
    {
        var session = AwaitingProvider(out _);
        session.Escalate("turn-budget-exhausted", T2);

        var payload = Assert.IsType<ProviderState.Escalated>(session.GetState());
        Assert.Equal("{}", payload.PartialProfileJson);
    }

    [Fact]
    public void Escalate_ThrowsFromComplete()
    {
        var session = AgentProcessing(out _, out _);
        session.Complete(Guid.NewGuid(), T3);

        Assert.Throws<InvalidOperationException>(
            () => session.Escalate("retroactive", T4));
    }

    [Fact]
    public void Escalate_ThrowsFromEscalated()
    {
        var session = AwaitingProvider(out _);
        session.Escalate("first", T2);
        Assert.Throws<InvalidOperationException>(
            () => session.Escalate("second", T3));
    }

    // ─────────────────────────────────────────── State payload round-trip ─

    [Fact]
    public void StatePayloadJson_RoundTripsThroughPolymorphicDeserialize()
    {
        // Tests that SetState's Serialize<ProviderState>(...) writes the
        // discriminator and a downstream EF rehydrate (simulated by direct
        // STJ deserialize) reconstructs the right subtype.
        var session = AgentProcessing(out _, out var turnId);

        var redeserialized = JsonSerializer.Deserialize<ProviderState>(
            session.StatePayloadJson, DomainJson.Options);

        var processing = Assert.IsType<ProviderState.AgentProcessing>(redeserialized);
        Assert.Equal(turnId, processing.TurnId);
        Assert.Equal(T2, processing.StartedAt);
    }

    [Fact]
    public void StatePayloadJson_DiscriminatorIsKindOfCurrentState()
    {
        var session = AwaitingProvider(out _);

        using var doc = JsonDocument.Parse(session.StatePayloadJson);
        Assert.Equal(
            nameof(IntakeState.AwaitingProvider),
            doc.RootElement.GetProperty("kind").GetString());
    }

    // ──────────────────────────────────────────────────── helpers ───────

    private static IntakeSession Pending()
        => IntakeSession.Start(Guid.NewGuid(), turnBudget: 8, nowUtc: T0);

    private static IntakeSession AwaitingProvider(out Guid magicLinkId)
    {
        var session = Pending();
        magicLinkId = Guid.NewGuid();
        session.NotifyInvitationSent(magicLinkId, T1);
        return session;
    }

    private static IntakeSession AgentProcessing(out Guid magicLinkId, out Guid turnId)
    {
        var session = AwaitingProvider(out magicLinkId);
        turnId = Guid.NewGuid();
        session.BeginAgentTurn(turnId, T2);
        return session;
    }
}
