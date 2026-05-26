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
///
/// <para>Tools that fire a terminal action or contribute a proposal back
/// to the orchestrator implement <see cref="ITerminalTool"/> or
/// <see cref="IProposalTool"/> — those interfaces are how the runtime
/// pulls structured fields out of the tool's JSON result without
/// hard-coding tool names.</para>
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
    /// True iff this tool also implements <see cref="ITerminalTool"/>.
    /// Kept as a property so the runtime can do its
    /// "exactly-one-terminal" construction-time guard cheaply, without
    /// a type-test against every tool. Concrete tools should implement
    /// <see cref="ITerminalTool"/> instead of overriding this directly.
    /// </summary>
    bool IsTerminal => this is ITerminalTool;
}

/// <summary>
/// A tool that ends the agent loop on invocation. The runtime captures
/// the terminal result via <see cref="TryReadTerminalResult"/> and breaks
/// out without asking the LLM for another turn. Exactly one
/// <see cref="ITerminalTool"/> must be registered (enforced at agent
/// construction); today that's <c>compute_readiness</c>.
/// </summary>
public interface ITerminalTool : IIntakeTool
{
    /// <summary>
    /// Pull the structured terminal payload (today: the readiness score
    /// id) out of the tool's JSON result. Returning <c>false</c> means
    /// the terminal fired but produced no usable payload — the runtime
    /// logs and escalates rather than silently demoting to "empty turn."
    /// </summary>
    bool TryReadTerminalResult(JsonElement result, out Guid completedScoreId);
}

/// <summary>
/// A non-terminal tool that contributes a proposal back to the
/// orchestrator. Today that's <c>compose_followup</c>, which proposes an
/// outbound email; the orchestrator (<c>IntakeTurnJob</c>, C5) commits
/// the proposal as an <c>OutboundMessage</c> on the way out of the turn.
/// </summary>
public interface IProposalTool : IIntakeTool
{
    /// <summary>
    /// Pull the proposal's subject/body out of the tool's JSON result.
    /// Returning <c>false</c> means the tool ran but didn't produce a
    /// proposal — the runtime ignores it and the next agent iteration
    /// can retry.
    /// </summary>
    bool TryReadProposal(JsonElement result, out string subject, out string body);
}
