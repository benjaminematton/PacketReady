namespace PacketReady.Domain.Audit;

/// <summary>
/// Canonical event-type strings. Plain TEXT in the DB (no enum constraint) so new
/// event types ship without a migration — this file is the runtime contract.
/// </summary>
public static class AuditEventType
{
    // Phase 0
    public const string PingExecuted = "PingExecuted";

    // Phase 1
    public const string ScoreComputed = "ScoreComputed";

    // Phase 3
    public const string DocumentUploaded = "DocumentUploaded";

    // Phase 5 — intake lifecycle.
    //
    // Stamped per FSM transition + per outbound dispatch (DoD item 9).
    // IntakeTurnCompleted is the per-turn telemetry summary (steps,
    // tokens, wall clock); the three transition events
    // (IntakeTurnStarted / IntakeCompleted / IntakeEscalated /
    // IntakeFollowupQueued) carry the FSM signal. Pairing both in the
    // audit log lets the dashboard's side panel reconstruct "what did
    // the system do for provider X" without re-deriving the FSM walk
    // from outbox + score rows.
    public const string IntakeStarted = "IntakeStarted";
    public const string IntakeTurnStarted = "IntakeTurnStarted";
    public const string IntakeTurnCompleted = "IntakeTurnCompleted";
    public const string IntakeCompleted = "IntakeCompleted";
    public const string IntakeFollowupQueued = "IntakeFollowupQueued";
    public const string IntakeEscalated = "IntakeEscalated";
    public const string OutboundMessageSent = "OutboundMessageSent";
}
