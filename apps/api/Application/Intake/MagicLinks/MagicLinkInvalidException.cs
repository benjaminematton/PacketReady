namespace PacketReady.Application.Intake.MagicLinks;

/// <summary>
/// Raised by <see cref="IMagicLinkAuthority.ValidateAsync"/> on any path
/// that should produce a <c>410 Gone</c>. The portal endpoint catches this
/// and shapes it through <c>ProblemResults.MagicLinkInvalid</c>.
/// </summary>
public sealed class MagicLinkInvalidException : Exception
{
    public MagicLinkInvalidReason Reason { get; }

    public MagicLinkInvalidException(MagicLinkInvalidReason reason, string? detail = null)
        : base(detail ?? $"Magic link invalid: {reason}.")
    {
        Reason = reason;
    }
}
