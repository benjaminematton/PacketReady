namespace PacketReady.Domain.Intake;

/// <summary>
/// The five FSM states from <c>design.md §7.3</c>. Domain-state enum — stored
/// <c>PascalCase</c> in the <c>intake_sessions.state</c> column via
/// <see cref="Microsoft.EntityFrameworkCore.RelationalPropertyBuilderExtensions"/>'s
/// default <c>HasConversion&lt;string&gt;()</c> path. See <c>docs/conventions.md §1</c>
/// for why the on-disk string is PascalCase: there is no external authority for
/// these names; the C# member is the canonical string.
///
/// <para>Mirrored by the discriminated-union payload <see cref="ProviderState"/>.
/// The aggregate keeps the two in sync — every transition updates the enum and
/// the payload atomically.</para>
/// </summary>
public enum IntakeState
{
    Pending,
    AwaitingProvider,
    AgentProcessing,
    Complete,
    Escalated,
}
