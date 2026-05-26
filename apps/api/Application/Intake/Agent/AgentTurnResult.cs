namespace PacketReady.Application.Intake.Agent;

/// <summary>
/// What one agent turn left behind. The orchestrator
/// (<c>IntakeTurnJob</c>, C5) consumes this record to drive the FSM
/// transition: a terminal turn calls <c>IntakeSession.Complete</c>; a
/// proposed-followup turn issues a fresh <c>MagicLink</c> +
/// <c>OutboundMessage</c> and calls
/// <c>IntakeSession.EndAgentTurn(ContinueWithMagicLinkId=...)</c>; an
/// empty turn (neither terminal nor followup) escalates.
///
/// <para>The agent runtime stays out of the DB-write path — it reasons,
/// it returns the proposal, the orchestrator commits. Keeps the
/// runtime's blast radius bounded and makes it possible to mock the
/// agent in C5's IntakeTurnJob integration tests without spinning up an
/// LLM.</para>
/// </summary>
public sealed record AgentTurnResult(
    Guid TurnId,
    bool IsTerminal,
    Guid? CompletedReadinessScoreId,
    string? ProposedFollowupSubject,
    string? ProposedFollowupBody,
    int StepsConsumed,
    int InputTokensConsumed,
    int OutputTokensConsumed,
    TimeSpan WallClockConsumed)
{
    /// <summary>
    /// True when the agent composed a followup but didn't go terminal —
    /// the orchestrator should issue a new magic link and queue the
    /// outbound message.
    /// </summary>
    public bool HasProposedFollowup =>
        !IsTerminal
        && !string.IsNullOrEmpty(ProposedFollowupSubject)
        && !string.IsNullOrEmpty(ProposedFollowupBody);
}
