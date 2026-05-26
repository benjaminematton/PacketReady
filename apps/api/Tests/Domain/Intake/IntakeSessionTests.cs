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

    /// <summary>
    /// Walk all five FSM variants. Each landed-in-this-state session must
    /// round-trip its payload through STJ polymorphism back to the same
    /// concrete subtype with the same fields. Polymorphism by discriminator
    /// is what makes EF rehydrate work; one variant passing doesn't prove
    /// the other four.
    /// </summary>
    [Fact]
    public void StatePayloadJson_RoundTripsAllFiveVariants()
    {
        // Pending — set by Start(...) at T0.
        var pending = Pending();
        AssertRoundTrips<ProviderState.Pending>(pending, p =>
            Assert.Equal(T0, p.CreatedAt));

        // AwaitingProvider — set by NotifyInvitationSent at T1.
        var awaiting = AwaitingProvider(out var linkId);
        AssertRoundTrips<ProviderState.AwaitingProvider>(awaiting, p =>
        {
            Assert.Equal(linkId, p.MagicLinkId);
            Assert.Equal(0, p.RemindersSent);
        });

        // AgentProcessing — set by BeginAgentTurn at T2.
        var processing = AgentProcessing(out _, out var turnId);
        AssertRoundTrips<ProviderState.AgentProcessing>(processing, p =>
        {
            Assert.Equal(turnId, p.TurnId);
            Assert.Equal(T2, p.StartedAt);
        });

        // Complete — terminal EndAgentTurn at T3.
        var complete = AgentProcessing(out _, out _);
        var scoreId = Guid.NewGuid();
        complete.EndAgentTurn(
            new AgentTurnOutcome { CompletedReadinessScoreId = scoreId }, T3);
        AssertRoundTrips<ProviderState.Complete>(complete, p =>
        {
            Assert.Equal(scoreId, p.ReadinessScoreId);
            Assert.Equal(T3, p.CompletedAt);
        });

        // Escalated — non-empty PartialProfileJson so the JSON-in-JSON path is
        // exercised (the string contains characters STJ has to escape).
        var escalated = AwaitingProvider(out _);
        escalated.Escalate("budget:steps", T4, partialProfileJson: "{\"name\":\"jane\"}");
        AssertRoundTrips<ProviderState.Escalated>(escalated, p =>
        {
            Assert.Equal("budget:steps", p.Reason);
            Assert.Equal("{\"name\":\"jane\"}", p.PartialProfileJson);
        });
    }

    [Theory]
    [InlineData(IntakeState.Pending)]
    [InlineData(IntakeState.AwaitingProvider)]
    [InlineData(IntakeState.AgentProcessing)]
    [InlineData(IntakeState.Complete)]
    [InlineData(IntakeState.Escalated)]
    public void StatePayloadJson_DiscriminatorMatchesStateColumn(IntakeState state)
    {
        var session = SessionInState(state);

        using var doc = JsonDocument.Parse(session.StatePayloadJson);
        Assert.Equal(
            state.ToString(),
            doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(state, session.State);
    }

    private static void AssertRoundTrips<TExpected>(
        IntakeSession session, Action<TExpected> assertFields)
        where TExpected : ProviderState
    {
        var redeserialized = JsonSerializer.Deserialize<ProviderState>(
            session.StatePayloadJson, DomainJson.Options);
        var concrete = Assert.IsType<TExpected>(redeserialized);
        assertFields(concrete);
    }

    private static IntakeSession SessionInState(IntakeState target)
    {
        switch (target)
        {
            case IntakeState.Pending:
                return Pending();
            case IntakeState.AwaitingProvider:
                return AwaitingProvider(out _);
            case IntakeState.AgentProcessing:
                return AgentProcessing(out _, out _);
            case IntakeState.Complete:
            {
                var s = AgentProcessing(out _, out _);
                s.EndAgentTurn(
                    new AgentTurnOutcome { CompletedReadinessScoreId = Guid.NewGuid() }, T3);
                return s;
            }
            case IntakeState.Escalated:
            {
                var s = AwaitingProvider(out _);
                s.Escalate("budget:steps", T4);
                return s;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
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
