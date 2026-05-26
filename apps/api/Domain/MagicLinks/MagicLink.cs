namespace PacketReady.Domain.MagicLinks;

/// <summary>
/// One issued magic-link row. The JWT the provider receives is signed with
/// the link <see cref="Id"/>; verification looks up the row by id and
/// checks <see cref="ExpiresAt"/> + <see cref="ConsumedAt"/>.
///
/// <para>Aggregate-root-shaped, but factory + single-use consume logic land
/// in P5 C3. This file is C1 scaffolding so EF's <c>ModelSnapshot</c>
/// tracks the <c>magic_links</c> table from the AddIntake migration on.
/// Single-use consume requires <c>SELECT FOR UPDATE</c> + null-check in a
/// transaction (see <c>phase-5-intake-agent.md</c> "Magic-link replay" risk).</para>
/// </summary>
public class MagicLink
{
    public Guid Id { get; private set; }
    public Guid ProviderId { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }

    private MagicLink() { }
}
