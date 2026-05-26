namespace PacketReady.Domain.Intake;

/// <summary>
/// In-memory descriptor for one agent turn. Not persisted as its own row in
/// P5 — the audit log (<c>audit_events</c>) carries the per-turn trail. The
/// record exists so handler code can pass the active turn context around
/// without unpacking <see cref="ProviderState.AgentProcessing"/> at every
/// call site.
/// </summary>
public sealed record IntakeTurn(Guid TurnId, DateTimeOffset StartedAt);
