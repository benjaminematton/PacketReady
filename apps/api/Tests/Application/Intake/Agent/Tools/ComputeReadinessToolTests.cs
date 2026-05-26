using System.Text.Json;
using MediatR;
using Moq;
using PacketReady.Application.Intake.Agent.Tools;
using PacketReady.Application.Scoring.Commands.ComputeReadinessScore;
using PacketReady.Domain.Scoring;
using Xunit;

namespace PacketReady.Tests.Application.Intake.Agent.Tools;

public class ComputeReadinessToolTests
{
    private static readonly Guid AmbientProviderId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TurnId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Invoke_HonorsAmbientProviderId_WhenArgsHaveADifferentOne()
    {
        // The agent might emit a different provider_id in its args (the
        // schema requires the field for shape correctness). The runtime
        // ALWAYS supplies the session's provider_id ambient; the tool
        // must score the ambient one, never the agent-supplied one.
        var ambientScored = Guid.Empty;
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(
                It.IsAny<ComputeReadinessScoreCommand>(),
                It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((cmd, _) =>
                ambientScored = ((ComputeReadinessScoreCommand)cmd).ProviderId)
            .ReturnsAsync(BuildScore(AmbientProviderId));

        var tool = new ComputeReadinessTool(mediator.Object);

        // Args say "score someone else" — runtime must ignore this.
        var maliciousArgs = JsonDocument.Parse(
            """{ "provider_id": "ffffffff-ffff-ffff-ffff-ffffffffffff" }""").RootElement;

        var result = await tool.InvokeAsync(maliciousArgs, AmbientProviderId, TurnId, CancellationToken.None);

        Assert.Equal(AmbientProviderId, ambientScored);
        var readResult = ((ITerminalTool)tool).TryReadTerminalResult(result, out var scoreId);
        Assert.True(readResult);
        Assert.NotEqual(Guid.Empty, scoreId);
    }

    [Fact]
    public void IsTerminal_ReturnsTrue()
    {
        IIntakeTool tool = new ComputeReadinessTool(Mock.Of<IMediator>());
        Assert.True(tool.IsTerminal);
        Assert.IsAssignableFrom<ITerminalTool>(tool);
    }

    private static ReadinessScoreDto BuildScore(Guid providerId) => new(
        Id: Guid.NewGuid(),
        ProviderId: providerId,
        Score: 92,
        Tier: Tier.Green,
        CriticalCount: 0,
        MajorCount: 0,
        MinorCount: 1,
        Issues: Array.Empty<Issue>(),
        ComputedAt: new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
}
