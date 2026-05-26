namespace PacketReady.Domain.Intake;

/// <summary>
/// Raised inside the agent tool-use loop when any per-turn budget axis is
/// exhausted (steps / tokens / wall-clock). Caught by <c>IntakeTurnJob</c>,
/// which transitions the session to <see cref="IntakeState.Escalated"/>
/// with the axis name attached.
/// </summary>
public sealed class BudgetExhaustedException : Exception
{
    public string Axis { get; }

    public BudgetExhaustedException(string axis)
        : base($"Intake turn exhausted budget axis: {axis}")
    {
        if (string.IsNullOrWhiteSpace(axis))
            throw new ArgumentException("Axis is required.", nameof(axis));
        Axis = axis;
    }
}
