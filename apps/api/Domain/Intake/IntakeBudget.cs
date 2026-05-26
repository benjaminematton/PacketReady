namespace PacketReady.Domain.Intake;

/// <summary>
/// Per-turn caps for the intake agent loop. Three axes — exceeding any one
/// aborts the turn and transitions the session to <see cref="IntakeState.Escalated"/>.
/// Matched to VaBene's <c>OnboardingTurnJob</c> budgets, picked so a runaway
/// loop costs &lt; $0.20 worst case.
///
/// <para>This is the <i>per-turn</i> budget. The <i>per-intake</i> total-turn
/// cap lives on <c>IntakeSession.TurnBudget</c> (default 8).</para>
/// </summary>
public sealed record IntakeBudget(int Steps, int Tokens, TimeSpan WallClock)
{
    public static readonly IntakeBudget Default = new(
        Steps: 15,
        Tokens: 80_000,
        WallClock: TimeSpan.FromSeconds(90));
}
