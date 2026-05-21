using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PacketReady.Application.Audit;
using PacketReady.Application.Ping;
using PacketReady.Domain.Audit;
using PacketReady.Infrastructure.Audit;
using PacketReady.Infrastructure.Persistence;
using PacketReady.Tests.Infrastructure;
using Xunit;

namespace PacketReady.Tests.Application;

public class PingCommandHandlerTests
{
    [Fact]
    public async Task Handle_WritesAuditRowAndReturnsTokenUsage()
    {
        var dbName = Guid.NewGuid().ToString();
        var factory = new InMemoryContextFactory(dbName);
        var scoped = factory.CreateDbContext();
        var auditWriter = new AuditWriter(scoped, factory, NullLogger<AuditWriter>.Instance);

        var chat = new Mock<IChatClient>(MockBehavior.Strict);
        var fakeResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi there"))
        {
            Usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 9 },
        };
        chat
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeResponse);

        var handler = new PingCommandHandler(
            chat.Object,
            auditWriter,
            scoped,
            NullLogger<PingCommandHandler>.Instance);

        var result = await handler.Handle(new PingCommand("hello"), CancellationToken.None);

        Assert.Equal("hi there", result.Reply);
        Assert.Equal("claude-haiku-4-5", result.Model);
        Assert.Equal(12, result.InputTokens);
        Assert.Equal(9, result.OutputTokens);
        Assert.NotEqual(Guid.Empty, result.AuditEventId);
        // 12/1M * $1 + 9/1M * $5 = $0.000012 + $0.000045 = $0.000057
        Assert.Equal(0.000057m, result.CostUsd);

        using var observer = factory.CreateDbContext();
        var row = await observer.AuditEvents.SingleAsync(e => e.Id == result.AuditEventId);
        Assert.Equal(AuditEventType.PingExecuted, row.EventType);
        Assert.Contains("hi there", row.Payload);
        Assert.Contains("hello", row.Payload);
    }

    [Fact]
    public async Task Handle_TolerantOfMissingUsage()
    {
        var dbName = Guid.NewGuid().ToString();
        var factory = new InMemoryContextFactory(dbName);
        var scoped = factory.CreateDbContext();
        var auditWriter = new AuditWriter(scoped, factory, NullLogger<AuditWriter>.Instance);

        var chat = new Mock<IChatClient>();
        chat
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var handler = new PingCommandHandler(
            chat.Object, auditWriter, scoped, NullLogger<PingCommandHandler>.Instance);

        var result = await handler.Handle(new PingCommand("hello"), CancellationToken.None);

        Assert.Equal(0, result.InputTokens);
        Assert.Equal(0, result.OutputTokens);
        Assert.Equal(0m, result.CostUsd);
    }
}
