namespace PacketReady.Application.Abstractions;

/// <summary>
/// Single commit point per request. Handlers stage writes via repositories and
/// audit-writers (which add to the same scoped DbContext under the hood), then call
/// <see cref="SaveChangesAsync"/> once at the end. Implementation lives in
/// Infrastructure so Application has no compile-time dependency on EF Core types.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct);
}
