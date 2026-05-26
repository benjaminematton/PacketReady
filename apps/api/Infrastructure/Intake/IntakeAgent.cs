using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Intake.Agent;
using PacketReady.Application.Intake.Agent.Tools;
using PacketReady.Application.Prompts;
using PacketReady.Domain.Intake;

namespace PacketReady.Infrastructure.Intake;

/// <summary>
/// The agent runtime — one <see cref="RunTurnAsync"/> call drives a manual
/// tool-use loop against Microsoft.Extensions.AI's <see cref="IChatClient"/>
/// (which wraps Anthropic.SDK) until one of three things happens:
/// <list type="bullet">
///   <item>A terminal tool fires (<c>compute_readiness</c>) — capture the
///         readiness score id, break.</item>
///   <item>The model returns a turn with no tool calls — the agent
///         decided to stop without composing a followup or scoring; the
///         orchestrator escalates the empty turn.</item>
///   <item>A per-turn budget axis (steps / tokens / wall) exhausts —
///         throw <see cref="BudgetExhaustedException"/>; the orchestrator
///         catches and escalates with the axis name.</item>
/// </list>
///
/// <para><b>Tool dispatching.</b> Each registered <see cref="IIntakeTool"/>
/// is wrapped as an <see cref="AIFunction"/> on the way into ChatOptions;
/// the model's <see cref="FunctionCallContent"/> emissions route back to
/// <see cref="IIntakeTool.InvokeAsync"/> via the name lookup. An unknown
/// tool name (the model occasionally invents one — design.md §7.4
/// "miss-selection") gets a structured error result, and the agent's next
/// iteration sees the refusal and retries.</para>
///
/// <para><b>No DB writes.</b> The runtime reasons; the orchestrator
/// commits. <see cref="AgentTurnResult"/> carries the proposal back as
/// plain fields — the orchestrator (<c>IntakeTurnJob</c>, C5) issues any
/// new magic link + outbox row and saves the FSM transition.</para>
/// </summary>
public sealed class IntakeAgent : IIntakeAgent
{
    public const string ModelId = "claude-sonnet-4-6";

    private static readonly IntakeBudget Budget = IntakeBudget.Default;
    private const int MaxOutputTokensPerStep = 4096;

    private readonly IChatClient _chat;
    private readonly IPromptLoader _prompts;
    private readonly IReadOnlyList<IIntakeTool> _tools;
    private readonly TimeProvider _clock;
    private readonly ILogger<IntakeAgent> _logger;

    public IntakeAgent(
        IChatClient chat,
        IPromptLoader prompts,
        IEnumerable<IIntakeTool> tools,
        TimeProvider clock,
        ILogger<IntakeAgent> logger)
    {
        _chat = chat;
        _prompts = prompts;
        _clock = clock;
        _logger = logger;
        _tools = tools.ToList();

        if (_tools.Count == 0)
            throw new InvalidOperationException(
                "IntakeAgent requires at least one tool registered. Did AddIntakeAgent run?");

        var terminalCount = _tools.OfType<ITerminalTool>().Count();
        if (terminalCount != 1)
            throw new InvalidOperationException(
                "Exactly one ITerminalTool must be registered (compute_readiness). " +
                $"Found {terminalCount}.");
    }

    public async Task<AgentTurnResult> RunTurnAsync(
        Guid providerId,
        Guid turnId,
        CancellationToken ct = default)
    {
        if (providerId == Guid.Empty)
            throw new ArgumentException("ProviderId is required.", nameof(providerId));
        if (turnId == Guid.Empty)
            throw new ArgumentException("TurnId is required.", nameof(turnId));

        var startTimestamp = _clock.GetTimestamp();
        var systemPrompt = await _prompts.LoadAsync(PromptKeys.IntakeAgent, ct);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User,
                $"Run one intake turn for provider {providerId:D}. " +
                "Inspect their uploaded documents (read_document is your starting point), " +
                "verify identity against primary sources where useful, " +
                "and either compose a single followup with the gap list or call compute_readiness."),
        };

        // Declaration-only: the runtime is the sole dispatcher (model-emitted
        // FunctionCallContent routes back through InvokeAsync on the
        // IIntakeTool directly), so we ship MEAI a non-invocable declaration
        // and reuse the tool's hand-written InputSchema verbatim. Going
        // through AIFunctionFactory.Create(delegate, ...) would derive the
        // schema from the lambda's parameters and silently drop the
        // required/enum constraints — the model would see "args:object" and
        // the tool would see args under the wrong key.
        var aiTools = _tools
            .Select(t => (AITool)AIFunctionFactory.CreateDeclaration(
                name: t.Name,
                description: t.Description,
                jsonSchema: t.InputSchema))
            .ToList();

        var options = new ChatOptions
        {
            ModelId = ModelId,
            Tools = aiTools,
            ToolMode = ChatToolMode.Auto,
            MaxOutputTokens = MaxOutputTokensPerStep,
            Temperature = 0.0f,
        };

        var steps = 0;
        var inputTokens = 0;
        var outputTokens = 0;
        Guid? terminalScoreId = null;
        string? proposedSubject = null;
        string? proposedBody = null;

        while (true)
        {
            CheckBudget(steps, inputTokens + outputTokens, _clock.GetElapsedTime(startTimestamp));

            ChatResponse response;
            try
            {
                response = await _chat.GetResponseAsync(messages, options, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "IntakeAgent chat call failed at step {Step} for provider {ProviderId}",
                    steps, providerId);
                throw;
            }

            steps++;
            inputTokens += (int)(response.Usage?.InputTokenCount ?? 0);
            outputTokens += (int)(response.Usage?.OutputTokenCount ?? 0);

            var calls = ExtractFunctionCalls(response);

            // No tool calls = the model emitted a final assistant text. The
            // prompt steers it to invoke a terminal tool instead — landing
            // here means the model exhausted its reasoning without picking.
            // Surface as "empty turn" to the orchestrator.
            if (calls.Count == 0)
            {
                _logger.LogWarning(
                    "Agent turn ended without a tool call (steps={Steps}, tokens={InTokens}+{OutTokens}); orchestrator should escalate.",
                    steps, inputTokens, outputTokens);
                break;
            }

            // Mirror the assistant's messages (with tool_use blocks) into the
            // running transcript so the next loop iteration sees the
            // model's prior call when we send back tool_result.
            foreach (var msg in response.Messages)
                messages.Add(msg);

            var terminalFired = false;

            foreach (var call in calls)
            {
                CheckBudget(steps, inputTokens + outputTokens, _clock.GetElapsedTime(startTimestamp));

                JsonElement result;
                var tool = _tools.FirstOrDefault(t =>
                    string.Equals(t.Name, call.Name, StringComparison.Ordinal));

                if (tool is null)
                {
                    // Miss-selection: the agent invented a tool. Refuse with
                    // a structured error; the next iteration sees it and
                    // tries again from the registered 5.
                    var known = string.Join(", ", _tools.Select(t => t.Name));
                    result = ToolResults.Error(
                        $"Unknown tool '{call.Name}'. Available tools: {known}.");
                    _logger.LogWarning(
                        "Agent miss-selection: unknown tool '{ToolName}' at step {Step}",
                        call.Name, steps);
                }
                else
                {
                    var argsEl = ToolArgsFromCall(call);
                    try
                    {
                        result = await tool.InvokeAsync(argsEl, providerId, turnId, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Tool {ToolName} threw at step {Step}", call.Name, steps);
                        result = ToolResults.Error(
                            $"Tool '{call.Name}' failed: {ex.Message}. Pick another path.");
                    }

                    if (tool is ITerminalTool terminal)
                    {
                        if (!terminal.TryReadTerminalResult(result, out var parsed))
                        {
                            // Terminal tool fired but emitted no usable payload.
                            // Don't silently demote to "empty turn" — a terminal
                            // with no score id is a tool-contract violation,
                            // surface it loud so the orchestrator escalates.
                            _logger.LogError(
                                "Terminal tool {ToolName} fired without a readable result at step {Step}; result was {Result}",
                                tool.Name, steps, result.GetRawText());
                            throw new InvalidOperationException(
                                $"Terminal tool '{tool.Name}' returned a result without a readable terminal payload.");
                        }
                        terminalFired = true;
                        terminalScoreId = parsed;
                    }
                    else if (tool is IProposalTool proposal
                             && proposal.TryReadProposal(result, out var subject, out var body))
                    {
                        proposedSubject = subject;
                        proposedBody = body;
                    }
                }

                // Feed the tool_result back. Pass the JsonElement directly
                // so MEAI emits structured JSON for Anthropic's tool_result
                // content (not a quoted JSON-as-string).
                messages.Add(new ChatMessage(ChatRole.Tool, new List<AIContent>
                {
                    new FunctionResultContent(call.CallId, result),
                }));
            }

            if (terminalFired) break;
        }

        return new AgentTurnResult(
            TurnId: turnId,
            IsTerminal: terminalScoreId is not null,
            CompletedReadinessScoreId: terminalScoreId,
            ProposedFollowupSubject: proposedSubject,
            ProposedFollowupBody: proposedBody,
            StepsConsumed: steps,
            InputTokensConsumed: inputTokens,
            OutputTokensConsumed: outputTokens,
            WallClockConsumed: _clock.GetElapsedTime(startTimestamp));
    }

    private static void CheckBudget(int steps, int tokens, TimeSpan wall)
    {
        if (steps >= Budget.Steps) throw new BudgetExhaustedException("steps");
        if (tokens >= Budget.Tokens) throw new BudgetExhaustedException("tokens");
        if (wall >= Budget.WallClock) throw new BudgetExhaustedException("wall");
    }

    private static List<FunctionCallContent> ExtractFunctionCalls(ChatResponse response)
    {
        var calls = new List<FunctionCallContent>();
        foreach (var msg in response.Messages)
        {
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fcc)
                    calls.Add(fcc);
            }
        }
        return calls;
    }

    // MEAI's FunctionCallContent.Arguments is IDictionary<string, object?>.
    // Reflow to a JsonElement so each tool gets the JSON shape its
    // InputSchema describes, without forcing every tool to learn the MEAI
    // surface.
    private static JsonElement ToolArgsFromCall(FunctionCallContent call)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(call.Arguments ?? new Dictionary<string, object?>());
        return JsonDocument.Parse(bytes).RootElement;
    }
}
