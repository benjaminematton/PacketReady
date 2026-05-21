using PacketReady.Domain.Audit;

namespace PacketReady.Application.Audit;

/// <summary>
/// Append-only writer for the audit log. Two paths with deliberately different
/// consistency semantics:
/// <list type="bullet">
///   <item><see cref="Stage"/> — stages the row on the caller's DbContext. Commits
///   when the caller's unit-of-work saves. Use when audit + business state must be
///   atomic. Synchronous: nothing is actually awaited.</item>
///   <item><see cref="AppendAsync"/> — opens its own scope and commits independently.
///   Use for fire-and-forget telemetry where the audit must land even if the caller
///   rolls back. Swallows transport errors and logs; never throws into the caller.</item>
/// </list>
/// </summary>
public interface IAuditWriter
{
    /// <summary>Stages <paramref name="evt"/> on the current scope's DbContext. Caller's SaveChanges commits it.</summary>
    /// <returns>The event's id, for convenience when the caller wants to surface it before commit.</returns>
    Guid Stage(AuditEvent evt);

    /// <summary>Writes <paramref name="evt"/> on a fresh scope and commits immediately. Errors are logged, not thrown.</summary>
    /// <returns>The event's id. If the write failed, the row will not exist, but the id is still returned for log correlation.</returns>
    Task<Guid> AppendAsync(AuditEvent evt, CancellationToken ct);
}
