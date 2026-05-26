namespace PacketReady.Application.Intake.Agent;

/// <summary>
/// Port for the agent runtime. One <see cref="RunTurnAsync"/> call is one
/// agent loop bounded by <see cref="Domain.Intake.IntakeBudget"/>; the
/// runtime impl
/// (<c>Infrastructure.Intake.IntakeAgent</c>) drives the Anthropic
/// tool-use loop until a terminal tool fires, the agent stops calling
/// tools, or a budget axis exhausts (steps / tokens / wall-clock).
///
/// <para>The orchestrator (<c>IntakeTurnJob</c>, C5) is the only legitimate
/// caller. Tests substitute via Mock&lt;IIntakeAgent&gt; — the runtime's
/// behavior is exercised in its own integration test.</para>
/// </summary>
public interface IIntakeAgent
{
    /// <summary>
    /// Execute one agent turn for the provider. The runtime loads context
    /// (uploaded documents + their latest extractions + prior turn
    /// artifacts), runs the LLM with the 5-tool surface, and returns when
    /// the loop terminates.
    ///
    /// <para>Throws <see cref="Domain.Intake.BudgetExhaustedException"/>
    /// when any per-turn budget axis trips. The orchestrator catches and
    /// escalates.</para>
    /// </summary>
    Task<AgentTurnResult> RunTurnAsync(
        Guid providerId,
        Guid turnId,
        CancellationToken ct = default);
}
