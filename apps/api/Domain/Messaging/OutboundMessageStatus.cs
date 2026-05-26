namespace PacketReady.Domain.Messaging;

/// <summary>
/// Lifecycle states for an outbound message. The dispatcher
/// (<c>OutboxDispatcherJob</c>, P5 C5) sends rows where
/// <c>status = 'Queued' AND held_until &lt;= now()</c>, then flips them to
/// <see cref="Sent"/>. Admin yank inside the hold window flips to
/// <see cref="Cancelled"/>. <see cref="Held"/> is the transient state
/// during the dispatcher's send attempt.
/// </summary>
public enum OutboundMessageStatus
{
    Queued,
    Held,
    Sent,
    Cancelled,
}
