using System.Text.Json;

namespace PacketReady.Domain.Intake;

/// <summary>
/// Aggregate root for one provider's intake. One row per provider, enforced
/// by <c>UNIQUE (provider_id)</c> in the EF config. Carries the FSM state
/// (<see cref="State"/> + <see cref="StatePayloadJson"/>) plus the
/// turn-budget bookkeeping (<see cref="TurnsConsumed"/> / <see cref="TurnBudget"/>).
///
/// <para>The state is stored in two columns for two reasons. The
/// <c>state</c> enum column powers cheap queries ("show me all sessions in
/// <c>AwaitingProvider</c> for more than 3 days") without JSON path
/// operators. The <c>state_payload</c> JSONB column carries the
/// per-state-flavor data (e.g. the active magic-link id, the in-flight
/// turn id). Every transition updates both columns atomically through
/// <see cref="SetState"/> — the aggregate is the only legitimate writer.</para>
///
/// <para><b>Edges.</b> Legal transitions, with the method that effects each:</para>
/// <code>
///   (none)             ─ Start ─▶                Pending
///   Pending            ─ NotifyInvitationSent ─▶ AwaitingProvider
///   AwaitingProvider   ─ BeginAgentTurn       ─▶ AgentProcessing
///   AgentProcessing    ─ EndAgentTurn(cont)   ─▶ AwaitingProvider
///   AgentProcessing    ─ EndAgentTurn(term)   ─▶ Complete           (delegates to Complete)
///   AgentProcessing    ─ Complete             ─▶ Complete
///   Pending |
///   AwaitingProvider | ─ Escalate             ─▶ Escalated
///   AgentProcessing
/// </code>
/// <para>Every other call throws <see cref="InvalidOperationException"/>.
/// Audit-event emission is the orchestrator's job (P5 task 9), not the
/// aggregate's — keeps <c>Domain</c> dependency-free of <c>Audit</c>.</para>
/// </summary>
public sealed class IntakeSession
{
    public const int DefaultTurnBudget = 8;

    public Guid Id { get; private set; }
    public Guid ProviderId { get; private set; }

    /// <summary>
    /// PascalCase enum column. Mirrors the discriminator of
    /// <see cref="StatePayloadJson"/>. Kept in sync by <see cref="SetState"/>.
    /// </summary>
    public IntakeState State { get; private set; }

    private string _statePayloadJson = "{}";
    private ProviderState? _stateCache;

    /// <summary>
    /// JSONB-serialized <see cref="ProviderState"/> using
    /// <see cref="DomainJson.Options"/>. Always written through the polymorphic
    /// base type so STJ emits the <c>kind</c> discriminator.
    /// </summary>
    public string StatePayloadJson
    {
        get => _statePayloadJson;
        private set
        {
            _statePayloadJson = value;
            _stateCache = null;
        }
    }

    public int TurnsConsumed { get; private set; }
    public int TurnBudget { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastTransitionAt { get; private set; }

    private IntakeSession() { }

    public static IntakeSession Start(Guid providerId, int turnBudget, DateTimeOffset nowUtc)
    {
        if (providerId == Guid.Empty)
            throw new ArgumentException("Provider id is required.", nameof(providerId));
        if (turnBudget < 1)
            throw new ArgumentOutOfRangeException(
                nameof(turnBudget), turnBudget, "Turn budget must be >= 1.");

        var session = new IntakeSession
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            TurnBudget = turnBudget,
            TurnsConsumed = 0,
            CreatedAt = nowUtc,
        };
        session.SetState(new ProviderState.Pending(nowUtc), nowUtc);
        return session;
    }

    /// <summary>
    /// Deserialized view of <see cref="StatePayloadJson"/>. Cached after the
    /// first call; the setter clears the cache so an EF rehydrate gets a
    /// fresh deserialization.
    /// </summary>
    public ProviderState GetState()
        => _stateCache ??= JsonSerializer.Deserialize<ProviderState>(_statePayloadJson, DomainJson.Options)
           ?? throw new InvalidOperationException(
               $"IntakeSession {Id} has invalid state_payload JSON; cannot deserialize.");

    public void NotifyInvitationSent(Guid magicLinkId, DateTimeOffset nowUtc)
    {
        RequireState(IntakeState.Pending, nameof(NotifyInvitationSent));
        if (magicLinkId == Guid.Empty)
            throw new ArgumentException("Magic link id is required.", nameof(magicLinkId));

        SetState(new ProviderState.AwaitingProvider(magicLinkId, RemindersSent: 0), nowUtc);
    }

    public void BeginAgentTurn(Guid turnId, DateTimeOffset nowUtc)
    {
        RequireState(IntakeState.AwaitingProvider, nameof(BeginAgentTurn));
        if (turnId == Guid.Empty)
            throw new ArgumentException("Turn id is required.", nameof(turnId));

        // Belt-and-braces: the orchestrator (IntakeTurnJob) checks this
        // before enqueuing the turn and escalates if exhausted. The
        // aggregate refuses to start a turn it can't legally count, so a
        // caller that skipped the check fails loud here instead of
        // silently consuming the 9th attempt.
        if (TurnsConsumed >= TurnBudget)
            throw new InvalidOperationException(
                $"Turn budget already exhausted ({TurnsConsumed}/{TurnBudget}); "
                + "the orchestrator should Escalate() before calling BeginAgentTurn.");

        TurnsConsumed++;
        SetState(new ProviderState.AgentProcessing(turnId, nowUtc), nowUtc);
    }

    public void EndAgentTurn(AgentTurnOutcome outcome, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        RequireState(IntakeState.AgentProcessing, nameof(EndAgentTurn));

        // Ambiguous: terminal AND a follow-up link both set. The aggregate
        // rejects this rather than picking a winner — see AgentTurnOutcome's
        // contract.
        if (outcome.CompletedReadinessScoreId is not null
            && outcome.ContinueWithMagicLinkId is not null)
            throw new ArgumentException(
                "AgentTurnOutcome cannot set both CompletedReadinessScoreId (terminal) "
                + "and ContinueWithMagicLinkId (continuation).",
                nameof(outcome));

        if (outcome.IsTerminal)
        {
            // CompletedReadinessScoreId is non-null when IsTerminal — delegate.
            Complete(outcome.CompletedReadinessScoreId!.Value, nowUtc);
            return;
        }

        if (outcome.ContinueWithMagicLinkId is { } mid)
        {
            if (mid == Guid.Empty)
                throw new ArgumentException(
                    "ContinueWithMagicLinkId must be non-empty when set.", nameof(outcome));

            // RemindersSent resets on every turn-bounce: each return to
            // AwaitingProvider is a fresh wait cycle (a new follow-up was
            // just composed). Reminder count is "nudges since the last
            // outbound asked for something," not a lifetime counter.
            SetState(new ProviderState.AwaitingProvider(mid, RemindersSent: 0), nowUtc);
            return;
        }

        throw new ArgumentException(
            "AgentTurnOutcome must specify either CompletedReadinessScoreId (terminal) "
            + "or ContinueWithMagicLinkId (continuation).",
            nameof(outcome));
    }

    public void Complete(Guid readinessScoreId, DateTimeOffset nowUtc)
    {
        RequireState(IntakeState.AgentProcessing, nameof(Complete));
        if (readinessScoreId == Guid.Empty)
            throw new ArgumentException("Readiness score id is required.", nameof(readinessScoreId));

        SetState(new ProviderState.Complete(readinessScoreId, nowUtc), nowUtc);
    }

    public void Escalate(string reason, DateTimeOffset nowUtc, string partialProfileJson = "{}")
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Escalation reason is required.", nameof(reason));

        if (State is IntakeState.Complete or IntakeState.Escalated)
            throw new InvalidOperationException(
                $"Cannot escalate from terminal state {State}.");

        SetState(
            new ProviderState.Escalated(reason, partialProfileJson ?? "{}"),
            nowUtc);
    }

    private void RequireState(IntakeState expected, string operation)
    {
        if (State != expected)
            throw new InvalidOperationException(
                $"{operation} requires state {expected}, but session is in {State}.");
    }

    private void SetState(ProviderState newState, DateTimeOffset nowUtc)
    {
        // Serialize as the polymorphic BASE type so STJ writes the "kind"
        // discriminator. Passing the concrete subtype would omit the
        // discriminator and break deserialization on the next round-trip.
        StatePayloadJson = JsonSerializer.Serialize<ProviderState>(newState, DomainJson.Options);
        State = newState.Kind;
        LastTransitionAt = nowUtc;
    }
}
