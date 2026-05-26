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
        var stubTerminal = MakeStub("compute_readiness", isTerminal: true,
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
        var stubCompose = MakeStub("compose_followup", isTerminal: false,
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
        var stubTerminal = MakeStub("compute_readiness", isTerminal: true,
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
        var stubLoop = MakeStub("read_document", isTerminal: false,
            result: """{ "doc_type": "License" }""");

        var chat = ChatThatLoops("read_document", """{ "document_id": "deadbeef-0000-0000-0000-000000000000" }""");

        var agent = BuildAgent(
            chat: chat,
            tools: [stubLoop, PlaceholderTerminalTool()]);

        var ex = await Assert.ThrowsAsync<BudgetExhaustedException>(
            () => agent.RunTurnAsync(ProviderId, TurnId));
        Assert.Equal("steps", ex.Axis);
    }

    [Fact]
    public async Task RunTurn_ExhaustsTokenBudget_ThrowsBudgetExhaustedTokens()
    {
        // Each canned response bills 50_000 input + 50_000 output tokens —
        // a single iteration jumps past IntakeBudget.Default.Tokens
        // (80_000). The second iteration's pre-call CheckBudget trips.
        var stubLoop = MakeStub("read_document", isTerminal: false,
            result: """{ "doc_type": "License" }""");

        var chat = ChatThatLoopsWithUsage(
            "read_document",
            """{ "document_id": "deadbeef-0000-0000-0000-000000000000" }""",
            inputTokens: 50_000,
            outputTokens: 50_000);

        var agent = BuildAgent(
            chat: chat,
            tools: [stubLoop, PlaceholderTerminalTool()]);

        var ex = await Assert.ThrowsAsync<BudgetExhaustedException>(
            () => agent.RunTurnAsync(ProviderId, TurnId));
        Assert.Equal("tokens", ex.Axis);
    }

    [Fact]
    public async Task RunTurn_ExhaustsWallBudget_ThrowsBudgetExhaustedWall()
    {
        // Drive the FakeTimeProvider past IntakeBudget.Default.WallClock
        // (90s) on every step. The first iteration is admitted; the
        // second iteration's pre-call CheckBudget trips on "wall."
        var clock = new FakeTimeProvider(T0);

        var stubLoop = new ClockAdvancingStubTool(
            "read_document", "{}", clock, TimeSpan.FromSeconds(95));

        var chat = ChatThatLoops("read_document",
            """{ "document_id": "deadbeef-0000-0000-0000-000000000000" }""");

        var agent = new IntakeAgent(
            chat,
            BuildPromptLoader(),
            new IIntakeTool[] { stubLoop, PlaceholderTerminalTool() },
            clock,
            NullLogger<IntakeAgent>.Instance);

        var ex = await Assert.ThrowsAsync<BudgetExhaustedException>(
            () => agent.RunTurnAsync(ProviderId, TurnId));
        Assert.Equal("wall", ex.Axis);
    }

    // ───────────────────────────────────────────── error recovery ───────

    [Fact]
    public async Task RunTurn_ToolThrows_FeedsErrorResultAndContinues()
    {
        // First call hits a tool that throws; the runtime catches, emits
        // a structured error, and the agent's next iteration calls the
        // terminal tool — turn completes cleanly with the score id.
        var scoreId = Guid.NewGuid();
        var throwingTool = new ThrowingStubTool("read_document",
            new InvalidOperationException("boom"));
        var terminal = MakeStub("compute_readiness", isTerminal: true,
            result: $$"""{ "readiness_score_id": "{{scoreId:D}}" }""");

        var chat = ChatSequence(
            ToolCallResponse("read_document", """{ "document_id": "00000000-0000-0000-0000-000000000001" }"""),
            ToolCallResponse("compute_readiness", $$"""{ "provider_id": "{{ProviderId:D}}" }"""));

        var agent = BuildAgent(chat: chat, tools: [throwingTool, terminal]);

        var result = await agent.RunTurnAsync(ProviderId, TurnId);

        Assert.True(result.IsTerminal);
        Assert.Equal(scoreId, result.CompletedReadinessScoreId);
        Assert.Equal(2, result.StepsConsumed);
        Assert.Equal(1, throwingTool.CallCount);
    }

    // ───────────────────────────────────────────── parallel calls ───────

    [Fact]
    public async Task RunTurn_ParallelCalls_NonTerminalThenTerminal_TerminalWins()
    {
        // One assistant response carries two function_call blocks —
        // non-terminal first, terminal second. Both invoke; the terminal
        // breaks the loop after its tool_result is appended.
        var scoreId = Guid.NewGuid();
        var nonTerminal = MakeStub("read_document", isTerminal: false,
            result: """{ "doc_type": "License" }""");
        var terminal = MakeStub("compute_readiness", isTerminal: true,
            result: $$"""{ "readiness_score_id": "{{scoreId:D}}" }""");

        var parallelResponse = MultiToolCallResponse(
            ("read_document", """{ "document_id": "00000000-0000-0000-0000-000000000001" }"""),
            ("compute_readiness", $$"""{ "provider_id": "{{ProviderId:D}}" }"""));

        var chat = new Mock<IChatClient>();
        chat.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(parallelResponse);

        var agent = BuildAgent(chat: chat.Object, tools: [nonTerminal, terminal]);

        var result = await agent.RunTurnAsync(ProviderId, TurnId);

        Assert.True(result.IsTerminal);
        Assert.Equal(scoreId, result.CompletedReadinessScoreId);
        // 1 step (single chat response), but both tools fired inside it.
        Assert.Equal(1, result.StepsConsumed);
    }

    [Fact]
    public async Task RunTurn_TerminalWithoutPayload_ThrowsInvalidOperation()
    {
        // The terminal tool fired but the JSON result has no
        // readiness_score_id — that's a tool-contract violation. Loud
        // throw so the orchestrator escalates rather than silently
        // demoting to "empty turn."
        var bustedTerminal = MakeStub("compute_readiness", isTerminal: true,
            result: """{ "score": 50 }""");

        var chat = ChatThatCalls("compute_readiness",
            $$"""{ "provider_id": "{{ProviderId:D}}" }""");

        var agent = BuildAgent(
            chat: chat,
            tools: [PlaceholderNonTerminalTool(), bustedTerminal]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.RunTurnAsync(ProviderId, TurnId));
        Assert.Contains("compute_readiness", ex.Message);
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
        var t1 = MakeStub("a", isTerminal: true, result: "{}");
        var t2 = MakeStub("b", isTerminal: true, result: "{}");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildAgent(chat: new Mock<IChatClient>().Object, tools: [t1, t2]));
        Assert.Contains("Exactly one ITerminalTool", ex.Message);
    }

    [Fact]
    public void Ctor_RejectsZeroTerminals()
    {
        var t = MakeStub("a", isTerminal: false, result: "{}");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildAgent(chat: new Mock<IChatClient>().Object, tools: [t]));
        Assert.Contains("Exactly one ITerminalTool", ex.Message);
    }

    // ────────────────────────────────────────────── helpers ─────────────

    private static IntakeAgent BuildAgent(IChatClient chat, IReadOnlyList<IIntakeTool> tools)
    {
        return new IntakeAgent(
            chat,
            BuildPromptLoader(),
            tools,
            new FakeTimeProvider(T0),
            NullLogger<IntakeAgent>.Instance);
    }

    private static IPromptLoader BuildPromptLoader()
    {
        var prompts = new Mock<IPromptLoader>(MockBehavior.Strict);
        prompts.Setup(p => p.LoadAsync(PromptKeys.IntakeAgent, It.IsAny<CancellationToken>()))
            .ReturnsAsync("You are the intake agent. (test stub prompt)");
        return prompts.Object;
    }

    private static IIntakeTool PlaceholderTerminalTool() =>
        MakeStub("compute_readiness", isTerminal: true, "{}");

    private static IIntakeTool PlaceholderNonTerminalTool() =>
        MakeStub("read_document", isTerminal: false, "{}");

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

    private static IChatClient ChatThatLoopsWithUsage(
        string toolName, string argsJson, int inputTokens, int outputTokens)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(
                callId: "call_" + Guid.NewGuid().ToString("N")[..8],
                name: toolName,
                arguments: JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson)
                    ?? new Dictionary<string, object?>()),
        }))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
            },
        };
        var chat = new Mock<IChatClient>();
        chat.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
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

    private static ChatResponse MultiToolCallResponse(params (string Name, string ArgsJson)[] calls)
    {
        var content = new List<AIContent>();
        foreach (var (name, argsJson) in calls)
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson)
                ?? new Dictionary<string, object?>();
            content.Add(new FunctionCallContent(
                callId: "call_" + Guid.NewGuid().ToString("N")[..8],
                name: name,
                arguments: args));
        }
        var msg = new ChatMessage(ChatRole.Assistant, content);
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
    /// Minimal IIntakeTool that returns canned JSON. The static factories
    /// below return tools that also implement <see cref="ITerminalTool"/>
    /// or <see cref="IProposalTool"/> as appropriate — the agent now
    /// reads structured payloads via type-test, so a plain
    /// <c>IIntakeTool</c> with <c>IsTerminal=true</c> isn't enough on its
    /// own.
    /// </summary>
    private static StubTool MakeStub(string name, bool isTerminal, string result)
        => isTerminal
            ? new TerminalStubTool(name, result)
            : (StubTool)new ProposalCapableStubTool(name, result);

    private abstract class StubTool : IIntakeTool
    {
        private readonly string _resultJson;

        protected StubTool(string name, string result)
        {
            Name = name;
            _resultJson = result;
        }

        public string Name { get; }
        public string Description => $"Stub tool: {Name}";
        public JsonElement InputSchema { get; } =
            JsonDocument.Parse("""{ "type": "object", "properties": {} }""").RootElement;

        public Task<JsonElement> InvokeAsync(JsonElement args, Guid providerId, Guid turnId, CancellationToken ct)
            => Task.FromResult(JsonDocument.Parse(_resultJson).RootElement);
    }

    private sealed class TerminalStubTool : StubTool, ITerminalTool
    {
        public TerminalStubTool(string name, string result) : base(name, result) { }

        public bool TryReadTerminalResult(JsonElement result, out Guid completedScoreId)
        {
            completedScoreId = Guid.Empty;
            return result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("readiness_score_id", out var el)
                && el.ValueKind == JsonValueKind.String
                && Guid.TryParse(el.GetString(), out completedScoreId);
        }
    }

    private sealed class ProposalCapableStubTool : StubTool, IProposalTool
    {
        public ProposalCapableStubTool(string name, string result) : base(name, result) { }

        public bool TryReadProposal(JsonElement result, out string subject, out string body)
        {
            subject = string.Empty;
            body = string.Empty;
            if (result.ValueKind != JsonValueKind.Object) return false;
            if (!result.TryGetProperty("subject", out var s) || s.ValueKind != JsonValueKind.String) return false;
            if (!result.TryGetProperty("body", out var b) || b.ValueKind != JsonValueKind.String) return false;
            subject = s.GetString() ?? "";
            body = b.GetString() ?? "";
            return subject.Length > 0 && body.Length > 0;
        }
    }

    private sealed class ThrowingStubTool : IIntakeTool
    {
        private readonly Exception _toThrow;
        public int CallCount { get; private set; }

        public ThrowingStubTool(string name, Exception toThrow)
        {
            Name = name;
            _toThrow = toThrow;
        }

        public string Name { get; }
        public string Description => $"Throwing stub: {Name}";
        public JsonElement InputSchema { get; } =
            JsonDocument.Parse("""{ "type": "object", "properties": {} }""").RootElement;

        public Task<JsonElement> InvokeAsync(JsonElement args, Guid providerId, Guid turnId, CancellationToken ct)
        {
            CallCount++;
            throw _toThrow;
        }
    }

    private sealed class ClockAdvancingStubTool : IIntakeTool, IProposalTool
    {
        private readonly string _resultJson;
        private readonly FakeTimeProvider _clock;
        private readonly TimeSpan _advance;

        public ClockAdvancingStubTool(string name, string result, FakeTimeProvider clock, TimeSpan advance)
        {
            Name = name;
            _resultJson = result;
            _clock = clock;
            _advance = advance;
        }

        public string Name { get; }
        public string Description => $"Clock-advancing stub: {Name}";
        public JsonElement InputSchema { get; } =
            JsonDocument.Parse("""{ "type": "object", "properties": {} }""").RootElement;

        public Task<JsonElement> InvokeAsync(JsonElement args, Guid providerId, Guid turnId, CancellationToken ct)
        {
            _clock.Advance(_advance);
            return Task.FromResult(JsonDocument.Parse(_resultJson).RootElement);
        }

        public bool TryReadProposal(JsonElement result, out string subject, out string body)
        {
            subject = body = string.Empty;
            return false;
        }
    }
}
