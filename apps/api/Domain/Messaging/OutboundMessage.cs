namespace PacketReady.Domain.Messaging;

/// <summary>
/// One outbound message in the hold-at-send outbox. Aggregate-root-shaped,
/// but factory + invariants land in P5 C2 — this file is C1 scaffolding so
/// EF's <c>ModelSnapshot</c> tracks the <c>outbound_messages</c> table
/// alongside <c>intake_sessions</c>, and so the AddIntake migration is one
/// EF-coherent unit rather than three.
///
/// <para>Schema mirrors <c>design.md §7.5</c>. The dedup invariant is
/// <c>UNIQUE (provider_id, turn_id, kind)</c>; the dispatcher's send-eligibility
/// predicate is <c>status = 'Queued' AND held_until &lt;= now()</c>.</para>
/// </summary>
public class OutboundMessage
{
    public Guid Id { get; private set; }
    public Guid ProviderId { get; private set; }
    public Guid TurnId { get; private set; }
    public MessageKind Kind { get; private set; }
    public string Subject { get; private set; } = null!;
    public string Body { get; private set; } = null!;
    public OutboundMessageStatus Status { get; private set; }
    public DateTimeOffset? HeldUntil { get; private set; }
    public DateTimeOffset ComposedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    private OutboundMessage() { }
}
