using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using PacketReady.Application.Intake.Agent;
using PacketReady.Application.Intake.Agent.Tools;
using PacketReady.Application.Prompts;
using PacketReady.Domain.Intake;
using PacketReady.Infrastructure.Intake;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Intake;

public class IntakeAgentTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid ProviderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TurnId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ───────────────────────────────────────────── happy paths ──────────

    [Fact]
    public async Task RunTurn_TerminalToolFires_ReturnsIsTerminalWithScoreId()
    {
        var scoreId = Guid.NewGuid();
        var stubTerminal = new StubTool("compute_readiness", isTerminal: true,
            result: $$"""{ "readiness_score_id": "{{scoreId:D}}", "score": 87 }""");

        var agent = BuildAgent(
            chat: ChatThatCalls("compute_readiness", $$"""{ "provider_id": "{{ProviderId:D}}" }"""),
            tools: [PlaceholderNonTerminalTool(), stubTerminal]);

        var result = await agent.RunTurnAsync(ProviderId, TurnId);

        Assert.True(result.IsTerminal);
        Assert.Equal(scoreId, result.CompletedReadinessScoreId);
        Assert.Null(result.ProposedFollowupSubject);
        Assert.Equal(1, result.StepsConsumed);
    }

    [Fact]
    public async Task RunTurn_ComposeFollowup_CapturesSubjectAndBody()
    {
        var stubCompose = new StubTool("compose_followup", isTerminal: false,
            result: """{ "subject": "PacketReady — one more item", "body": "Hi there, ..." }""");

        // Model invokes compose_followup, then on the next iteration returns
        // text (no tool call) — the runtime treats that as "agent is done."
        var chat = ChatThatThenStops("compose_followup", """{ "gaps": [{"kind":"missing_dea","message":"need DEA"}] }""");

        var agent = BuildAgent(
            chat: chat,
            tools: [PlaceholderTerminalTool(), stubCompose]);

        var result = await agent.RunTurnAsync(ProviderId, TurnId);

        Assert.False(result.IsTerminal);
        Assert.Equal("PacketReady — one more item", result.ProposedFollowupSubject);
        Assert.Equal("Hi there, ...", result.ProposedFollowupBody);
        Assert.True(result.HasProposedFollowup);
    }

    [Fact]
    public async Task RunTurn_NoToolCallInResponse_ReturnsEmptyTurn()
    {
        // The orchestrator escalates this case — the result has no
        // terminal and no proposal.
        var chat = ChatThatReturnsText("I have nothing to do.");

        var agent = BuildAgent(
            chat: chat,
            tools: [PlaceholderNonTerminalTool(), PlaceholderTerminalTool()]);

        var result = await agent.RunTurnAsync(ProviderId, TurnId);

        Assert.False(result.IsTerminal);
        Assert.False(result.HasProposedFollowup);
        Assert.Null(result.CompletedReadinessScoreId);
    }

    // ───────────────────────────────────────────── miss-selection ───────

    [Fact]
    public async Task RunTurn_UnknownToolName_RefusesThenContinues()
    {
        // The first call invents a tool the dispatcher doesn't have.
        // The runtime returns a structured error result; the second call
        // picks compute_readiness from the registered set and the turn
        // terminates.
        var scoreId = Guid.NewGuid();
        var stubTerminal = new StubTool("compute_readiness", isTerminal: true,
            result: $$"""{ "readiness_score_id": "{{scoreId:D}}" }""");

        var chat = ChatSequence(
            ToolCallResponse("update_profile", "{}"),        // invented
            ToolCallResponse("compute_readiness", $$"""{ "provider_id": "{{ProviderId:D}}" }"""));

        var agent = BuildAgent(
            chat: chat,
            tools: [PlaceholderNonTerminalTool(), stubTerminal]);

        var result = await agent.RunTurnAsync(ProviderId, TurnId);

        Assert.True(result.IsTerminal);
        Assert.Equal(scoreId, result.CompletedReadinessScoreId);
        // 2 steps: the refusal + the successful terminal call.
        Assert.Equal(2, result.StepsConsumed);
    }

    // ───────────────────────────────────────────── budget ────────────────

    [Fact]
    public async Task RunTurn_ExhaustsStepBudget_ThrowsBudgetExhaustedSteps()
    {
        // Canned response repeats a non-terminal tool call forever. The
        // runtime trips the step cap (15 per IntakeBudget.Default) and
        // throws.
        var stubLoop = new StubTool("read_document", isTerminal: false,
            result: """{ "doc_type": "License" }""");

        var chat = ChatThatLoops("read_document", """{ "document_id": "deadbeef-0000-0000-0000-000000000000" }""");

        var agent = BuildAgent(
            chat: chat,
            tools: [stubLoop, PlaceholderTerminalTool()]);

        var ex = await Assert.ThrowsAsync<BudgetExhaustedException>(
            () => agent.RunTurnAsync(ProviderId, TurnId));
        Assert.Equal("steps", ex.Axis);
    }

    // ───────────────────────────────────────────── construction ────────

    [Fact]
    public void Ctor_RejectsNoTools()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildAgent(chat: new Mock<IChatClient>().Object, tools: []));
        Assert.Contains("at least one tool", ex.Message);
    }

    [Fact]
    public void Ctor_RejectsMultipleTerminals()
    {
        var t1 = new StubTool("a", isTerminal: true, result: "{}");
        var t2 = new StubTool("b", isTerminal: true, result: "{}");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildAgent(chat: new Mock<IChatClient>().Object, tools: [t1, t2]));
        Assert.Contains("Exactly one terminal", ex.Message);
    }

    [Fact]
    public void Ctor_RejectsZeroTerminals()
    {
        var t = new StubTool("a", isTerminal: false, result: "{}");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildAgent(chat: new Mock<IChatClient>().Object, tools: [t]));
        Assert.Contains("Exactly one terminal", ex.Message);
    }

    // ────────────────────────────────────────────── helpers ─────────────

    private static IntakeAgent BuildAgent(IChatClient chat, IReadOnlyList<IIntakeTool> tools)
    {
        var prompts = new Mock<IPromptLoader>(MockBehavior.Strict);
        prompts.Setup(p => p.LoadAsync(PromptKeys.IntakeAgent, It.IsAny<CancellationToken>()))
            .ReturnsAsync("You are the intake agent. (test stub prompt)");

        return new IntakeAgent(
            chat,
            prompts.Object,
            tools,
            new FakeTimeProvider(T0),
            NullLogger<IntakeAgent>.Instance);
    }

    private static StubTool PlaceholderTerminalTool() =>
        new("compute_readiness", isTerminal: true, result: "{}");

    private static StubTool PlaceholderNonTerminalTool() =>
        new("read_document", isTerminal: false, result: "{}");

    private static IChatClient ChatThatCalls(string toolName, string argsJson)
    {
        var chat = new Mock<IChatClient>();
        chat.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolCallResponse(toolName, argsJson));
        return chat.Object;
    }

    private static IChatClient ChatThatThenStops(string toolName, string argsJson)
    {
        var chat = new Mock<IChatClient>();
        chat.SetupSequence(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolCallResponse(toolName, argsJson))
            .ReturnsAsync(TextResponse("Done."));
        return chat.Object;
    }

    private static IChatClient ChatThatReturnsText(string text)
    {
        var chat = new Mock<IChatClient>();
        chat.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(TextResponse(text));
        return chat.Object;
    }

    private static IChatClient ChatThatLoops(string toolName, string argsJson)
    {
        var chat = new Mock<IChatClient>();
        chat.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolCallResponse(toolName, argsJson));
        return chat.Object;
    }

    private static IChatClient ChatSequence(params ChatResponse[] responses)
    {
        var chat = new Mock<IChatClient>();
        var seq = chat.SetupSequence(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()));
        foreach (var r in responses) seq = seq.ReturnsAsync(r);
        return chat.Object;
    }

    private static ChatResponse ToolCallResponse(string toolName, string argsJson)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson)
            ?? new Dictionary<string, object?>();
        var msg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(
                callId: "call_" + Guid.NewGuid().ToString("N")[..8],
                name: toolName,
                arguments: args),
        });
        return new ChatResponse(msg)
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 },
        };
    }

    private static ChatResponse TextResponse(string text)
    {
        var msg = new ChatMessage(ChatRole.Assistant, text);
        return new ChatResponse(msg)
        {
            Usage = new UsageDetails { InputTokenCount = 50, OutputTokenCount = 20 },
        };
    }

    /// <summary>
    /// Minimal IIntakeTool that returns canned JSON. Lets us drive the
    /// agent loop without spinning up the real tool stack (which needs
    /// DbContext + MediatR + primary-source mock).
    /// </summary>
    private sealed class StubTool : IIntakeTool
    {
        private readonly string _resultJson;

        public StubTool(string name, bool isTerminal, string result)
        {
            Name = name;
            IsTerminal = isTerminal;
            _resultJson = result;
        }

        public string Name { get; }
        public string Description => $"Stub tool: {Name}";
        public JsonElement InputSchema { get; } =
            JsonDocument.Parse("""{ "type": "object", "properties": {} }""").RootElement;
        public bool IsTerminal { get; }

        public Task<JsonElement> InvokeAsync(JsonElement args, Guid providerId, Guid turnId, CancellationToken ct)
            => Task.FromResult(JsonDocument.Parse(_resultJson).RootElement);
    }
}
