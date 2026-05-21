namespace PacketReady.Application.Scoring.Validators;

internal static class ClockExtensions
{
    public static DateOnly Today(this TimeProvider clock) =>
        DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
}
