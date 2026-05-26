namespace PacketReady.Domain.Messaging;

/// <summary>
/// Outbound-message kind. Domain-state enum (no external authority pinning
/// the strings) — stored <c>PascalCase</c> in the
/// <c>outbound_messages.kind</c> column. Three values cover the full
/// lifecycle: the invitation email when an intake starts, the consolidated
/// followup the agent composes mid-loop, and the "your file is ready"
/// notice the admin gets after <see cref="Intake.IntakeState.Complete"/>.
/// </summary>
public enum MessageKind
{
    IntakeInvitation,
    Followup,
    CompletionNotice,
}
