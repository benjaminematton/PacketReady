namespace PacketReady.Domain.Intake;

/// <summary>
/// Canonical wire-format strings for the <c>reason</c> column on an
/// escalated <see cref="IntakeSession"/> (and the matching
/// <c>IntakeEscalated</c> audit payload). One greppable home for the
/// vocabulary so a rename can't drift between the aggregate, the
/// orchestrator, the transitioner, and the audit log.
///
/// <para>Reasons are persisted into <c>state_payload</c> JSON and into
/// <c>audit_events.payload</c> — treat changes as schema migrations.
/// Test assertions stay as literals on purpose, so a value change
/// breaks the wire-format contract loudly.</para>
/// </summary>
public static class IntakeEscalationReason
{
    /// <summary>Pre-turn check found <c>TurnsConsumed &gt;= TurnBudget</c>.</summary>
    public const string TurnBudgetExhausted = "turn-budget-exhausted";

    /// <summary>Agent returned without a tool call or a followup proposal.</summary>
    public const string AgentEmptyTurn = "agent-empty-turn";

    /// <summary>
    /// Per-turn budget axis tripped mid-loop (raised as
    /// <see cref="BudgetExhaustedException"/>).
    /// </summary>
    public static string Budget(string axis) => $"budget:{axis}";

    /// <summary>
    /// Anything else the agent threw (LLM 5xx, tool-contract violation,
    /// socket error). <c>typeName</c> is the unqualified exception name.
    /// </summary>
    public static string AgentError(string typeName) => $"agent-error:{typeName}";
}
