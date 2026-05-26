namespace PacketReady.Domain.Messaging;

/// <summary>
/// Lifecycle states for an outbound message. The dispatcher
/// (<c>OutboxDispatcherJob</c>, P5 C5) sends rows where
/// <c>status = 'Queued' AND held_until &lt;= now()</c>, then flips them to
/// <see cref="Sent"/>. Admin yank inside the hold window flips to
/// <see cref="Cancelled"/>.
///
/// <para>There is no transient "Held" state: serialization between two
/// concurrent dispatchers is the row-lock's job (<c>SELECT … FOR UPDATE</c>
/// in <c>OutboxDispatcherJob</c>), and a 10-minute hold has no operator
/// dashboard semantics worth a third status column.</para>
/// </summary>
public enum OutboundMessageStatus
{
    Queued,
    Sent,
    Cancelled,
}
