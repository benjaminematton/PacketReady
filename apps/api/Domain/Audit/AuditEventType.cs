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

    // Phase 5
    public const string IntakeStarted = "IntakeStarted";
}
