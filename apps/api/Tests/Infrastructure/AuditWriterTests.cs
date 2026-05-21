using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PacketReady.Domain.Audit;
using PacketReady.Infrastructure.Audit;
using PacketReady.Infrastructure.Persistence;
using Xunit;

namespace PacketReady.Tests.Infrastructure;

public class AuditWriterTests
{
    private static (AuditWriter writer, PacketReadyDbContext scoped, InMemoryContextFactory factory)
        BuildWriter(string dbName)
    {
        var factory = new InMemoryContextFactory(dbName);
        var scoped = factory.CreateDbContext();
        var writer = new AuditWriter(scoped, factory, NullLogger<AuditWriter>.Instance);
        return (writer, scoped, factory);
    }

    [Fact]
    public void Stage_AddsRowButDoesNotPersistUntilSaveChanges()
    {
        var (writer, scoped, factory) = BuildWriter(Guid.NewGuid().ToString());

        var evt = AuditEvent.Create(AuditEventType.PingExecuted, "{\"ok\":true}");
        var id = writer.Stage(evt);

        Assert.Equal(evt.Id, id);

        // A second, independent context sees no row — the staged write is uncommitted.
        using var observer = factory.CreateDbContext();
        Assert.False(observer.AuditEvents.Any(e => e.Id == id));

        // After SaveChanges, the row is visible to a fresh context.
        scoped.SaveChanges();
        using var afterCommit = factory.CreateDbContext();
        Assert.True(afterCommit.AuditEvents.Any(e => e.Id == id));
    }

    [Fact]
    public async Task AppendAsync_PersistsImmediately()
    {
        var (writer, _, factory) = BuildWriter(Guid.NewGuid().ToString());

        var evt = AuditEvent.Create(AuditEventType.PingExecuted, "{}");
        var id = await writer.AppendAsync(evt, CancellationToken.None);

        using var observer = factory.CreateDbContext();
        var row = await observer.AuditEvents.SingleAsync(e => e.Id == id);
        Assert.Equal(AuditEventType.PingExecuted, row.EventType);
    }

    [Fact]
    public async Task AppendAsync_SwallowsTransportErrorsAndStillReturnsId()
    {
        // Force a failing context: dispose the factory's underlying DB by using a
        // factory that returns a context already-disposed at creation time.
        var brokenFactory = new BrokenFactory();
        var scoped = new InMemoryContextFactory("scoped-" + Guid.NewGuid()).CreateDbContext();
        var writer = new AuditWriter(scoped, brokenFactory, NullLogger<AuditWriter>.Instance);

        var evt = AuditEvent.Create(AuditEventType.PingExecuted, "{}");

        // Must not throw — fire-and-forget telemetry must isolate failures from callers.
        var id = await writer.AppendAsync(evt, CancellationToken.None);
        Assert.Equal(evt.Id, id);
    }

    [Fact]
    public async Task AppendAsync_PropagatesCancellation()
    {
        var (writer, _, _) = BuildWriter(Guid.NewGuid().ToString());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var evt = AuditEvent.Create(AuditEventType.PingExecuted, "{}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => writer.AppendAsync(evt, cts.Token));
    }

    private sealed class BrokenFactory : IDbContextFactory<PacketReadyDbContext>
    {
        public PacketReadyDbContext CreateDbContext()
            => throw new InvalidOperationException("simulated transport failure");
    }
}
