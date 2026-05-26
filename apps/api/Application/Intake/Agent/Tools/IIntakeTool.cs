using System.Text.Json;

namespace PacketReady.Application.Intake.Agent.Tools;

/// <summary>
/// One callable surface the LLM sees. Tool inventory is locked at exactly
/// 5 — see phase-5-intake-agent.md decision table; every extra tool
/// inflates the miss-selection rate.
///
/// <para>The runtime
/// (<c>Infrastructure.Intake.IntakeAgent</c>) collects every registered
/// <c>IIntakeTool</c> from DI, exposes each as an Anthropic-shaped tool
/// definition (name + description + input schema), dispatches
/// <c>tool_use</c> blocks to <see cref="InvokeAsync"/>, and feeds the
/// JSON result back as the next <c>tool_result</c> message. An unknown
/// tool name (the agent occasionally invents one) is refused by the
/// dispatcher with a structured error; the agent's next iteration sees
/// the refusal and retries.</para>
/// </summary>
public interface IIntakeTool
{
    /// <summary>Tool name as the LLM sees it. <c>snake_case</c>, must match the prompt + tool definition.</summary>
    string Name { get; }

    /// <summary>
    /// Short description shown to the LLM. Kept tight per
    /// phase-5-intake-agent.md "Risks": narrow descriptions cut
    /// miss-selection rate.
    /// </summary>
    string Description { get; }

    /// <summary>JSON Schema for the tool's input. Anthropic accepts the standard subset (type/properties/required/additionalProperties/items/enum/anyOf).</summary>
    JsonElement InputSchema { get; }

    /// <summary>
    /// Invoke the tool. <paramref name="args"/> is the LLM's JSON input
    /// (already shape-validated against <see cref="InputSchema"/> at the
    /// SDK boundary). The return value is serialized as the
    /// <c>tool_result</c> block content. Throw to fail the turn — the
    /// orchestrator catches and escalates with a partial-state audit row.
    /// </summary>
    Task<JsonElement> InvokeAsync(
        JsonElement args,
        Guid providerId,
        Guid turnId,
        CancellationToken ct);

    /// <summary>
    /// Terminal tools end the agent loop on invocation — the runtime
    /// captures the result + breaks out without asking the LLM for
    /// another turn. <c>compute_readiness</c> is the only terminal tool;
    /// every other returns <c>false</c>.
    /// </summary>
    bool IsTerminal => false;
}
