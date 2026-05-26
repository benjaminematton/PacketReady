namespace PacketReady.Domain.Intake;

/// <summary>
/// What the agent left behind after one turn. Consumed by
/// <c>IntakeSession.EndAgentTurn</c> to pick the next FSM state.
///
/// <para>Exactly one of <see cref="CompletedReadinessScoreId"/> or
/// <see cref="ContinueWithMagicLinkId"/> must be non-null. Both null is a
/// contradiction (the turn neither finished nor proposed continuation);
/// both set is ambiguous (terminal AND a followup link). The aggregate
/// validates this at the boundary.</para>
///
/// <para>P5's Application layer wraps this in an <c>AgentTurnResult</c> that
/// also carries tokens-consumed / cost / latency for telemetry. Domain
/// doesn't need any of that to commit the transition, so it stays out of
/// this record.</para>
/// </summary>
public sealed record AgentTurnOutcome
{
    /// <summary>
    /// The agent invoked <c>compute_readiness</c>. Carries the readiness
    /// score row id the terminal tool wrote. Non-null on terminal turns.
    /// </summary>
    public Guid? CompletedReadinessScoreId { get; init; }

    /// <summary>
    /// The agent proposed a followup. The session transitions back to
    /// <see cref="ProviderState.AwaitingProvider"/> with the supplied
    /// magic-link id (re-issued or carried forward by the orchestrator,
    /// per <c>design.md §6</c> "Magic link is re-usable").
    /// </summary>
    public Guid? ContinueWithMagicLinkId { get; init; }

    public bool IsTerminal => CompletedReadinessScoreId is not null;
}
