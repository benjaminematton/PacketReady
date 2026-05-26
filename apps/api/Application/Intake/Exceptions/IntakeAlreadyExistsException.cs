namespace PacketReady.Application.Intake.Exceptions;

/// <summary>
/// Thrown by <c>StartIntakeCommandHandler</c> when an
/// <c>intake_sessions</c> row already exists for the target provider. The
/// API layer catches this and maps to <c>409 Conflict</c> via
/// <c>ProblemResults.IntakeAlreadyExists</c>.
///
/// <para>The UNIQUE (provider_id) constraint
/// (<c>ux_intake_sessions_provider</c>) is the DB-side floor; the handler
/// pre-checks to surface the typed exception instead of letting a raw
/// <c>PostgresException</c> escape as an opaque 500.</para>
/// </summary>
public sealed class IntakeAlreadyExistsException(Guid providerId)
    : Exception($"An intake session already exists for provider {providerId}.")
{
    public Guid ProviderId { get; } = providerId;
}
