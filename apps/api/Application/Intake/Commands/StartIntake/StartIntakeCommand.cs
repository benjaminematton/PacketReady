using MediatR;

namespace PacketReady.Application.Intake.Commands.StartIntake;

/// <summary>
/// Admin action: start the intake lifecycle for an existing provider. Creates
/// an <c>IntakeSession</c> (state=<c>Pending</c>) plus a <c>MagicLink</c>
/// row in one transaction, and returns the signed token the admin needs
/// to share with the provider (the magic link URL).
///
/// <para><b>Out of C3 scope:</b> the doc's StartIntake also creates a
/// <c>Provider</c> row + an <c>intake_invitation</c> <c>OutboundMessage</c>.
/// Both are deferred: Provider creation needs an <c>Email</c> column +
/// <c>Provider.Open</c> factory (the demo seeds providers via
/// <c>tools/Seed</c> for now); OutboundMessage composition needs a
/// <c>ToAddress</c> field or a <c>Provider.Email</c> lookup. The agent +
/// dispatcher work in C4/C5 will pick those up.</para>
/// </summary>
public sealed record StartIntakeCommand(Guid ProviderId) : IRequest<StartIntakeResult>;

/// <summary>
/// Returned to the admin. <see cref="Token"/> is the signed magic-link
/// payload — the admin sends this in the URL the provider clicks.
/// </summary>
public sealed record StartIntakeResult(
    Guid ProviderId,
    Guid IntakeSessionId,
    Guid MagicLinkId,
    string Token,
    DateTimeOffset ExpiresAt);
