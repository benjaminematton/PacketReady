using System.Text.Json;
using System.Text.Json.Serialization;
using PacketReady.Domain.Audit;

namespace PacketReady.Application.Intake.Audit;

/// <summary>
/// Typed payload for <see cref="AuditEventType.IntakeEscalated"/>. Three
/// ways an intake escalates today:
/// <list type="bullet">
///   <item><b>turn-budget-exhausted</b> — pre-turn check found
///         <c>TurnsConsumed >= TurnBudget</c>. The orchestrator
///         escalated without invoking the agent.</item>
///   <item><b>budget:&lt;axis&gt;</b> — agent threw
///         <see cref="Domain.Intake.BudgetExhaustedException"/>; the per-turn
///         steps / tokens / wall-clock cap tripped mid-loop.</item>
///   <item><b>agent-empty-turn</b> — agent returned without a tool call
///         or a followup proposal.</item>
///   <item><b>agent-error:&lt;ExceptionType&gt;</b> — anything else the
///         agent threw (LLM 5xx, tool-contract violation, socket
///         error). The session would otherwise be stuck in
///         <c>AgentProcessing</c> forever per the
///         [AutomaticRetry(Attempts=0)] policy.</item>
/// </list>
///
/// <para><see cref="TurnId"/> is null when the pre-turn check escalates
/// without ever calling <c>BeginAgentTurn</c>; non-null otherwise.</para>
/// </summary>
public sealed record IntakeEscalatedPayload(
    [property: JsonPropertyName("provider_id")] Guid ProviderId,
    [property: JsonPropertyName("intake_session_id")] Guid IntakeSessionId,
    [property: JsonPropertyName("turn_id")] Guid? TurnId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("turns_consumed")] int TurnsConsumed,
    [property: JsonPropertyName("turn_budget")] int TurnBudget)
{
    public string ToJson() => JsonSerializer.Serialize(this);
}
