using System.Text.Json.Serialization;

namespace PacketReady.Domain.Intake;

/// <summary>
/// Discriminated union over the five FSM states. Serialized to
/// <c>intake_sessions.state_payload</c> JSONB via STJ polymorphism: the
/// <c>kind</c> property carries the discriminator string, the rest of the
/// row carries the per-state data.
///
/// <para>Mirrors the TypeScript shape in <c>design.md §7.3</c>. The C# enum
/// member name is the discriminator (PascalCase, matches the
/// <see cref="IntakeState"/> column) so payloads round-trip stably across
/// rename-safe enum-string converters.</para>
///
/// <para>Inner records are <c>sealed</c> so STJ's <c>[JsonDerivedType]</c>
/// covers the full type hierarchy. A new variant requires a new
/// <c>[JsonDerivedType]</c> entry here and a new enum member in
/// <see cref="IntakeState"/>; the aggregate's transition methods must also
/// learn the new edge.</para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Pending), nameof(IntakeState.Pending))]
[JsonDerivedType(typeof(AwaitingProvider), nameof(IntakeState.AwaitingProvider))]
[JsonDerivedType(typeof(AgentProcessing), nameof(IntakeState.AgentProcessing))]
[JsonDerivedType(typeof(Complete), nameof(IntakeState.Complete))]
[JsonDerivedType(typeof(Escalated), nameof(IntakeState.Escalated))]
public abstract record ProviderState
{
    /// <summary>
    /// In-memory mirror of the persisted <see cref="IntakeState"/> column.
    /// <c>[JsonIgnore]</c> so the discriminator isn't doubled — STJ emits
    /// its own <c>kind</c> via <see cref="JsonPolymorphicAttribute"/>.
    /// </summary>
    [JsonIgnore]
    public abstract IntakeState Kind { get; }

    public sealed record Pending(DateTimeOffset CreatedAt) : ProviderState
    {
        [JsonIgnore]
        public override IntakeState Kind => IntakeState.Pending;
    }

    public sealed record AwaitingProvider(Guid MagicLinkId, int RemindersSent) : ProviderState
    {
        [JsonIgnore]
        public override IntakeState Kind => IntakeState.AwaitingProvider;
    }

    public sealed record AgentProcessing(Guid TurnId, DateTimeOffset StartedAt) : ProviderState
    {
        [JsonIgnore]
        public override IntakeState Kind => IntakeState.AgentProcessing;
    }

    public sealed record Complete(Guid ReadinessScoreId, DateTimeOffset CompletedAt) : ProviderState
    {
        [JsonIgnore]
        public override IntakeState Kind => IntakeState.Complete;
    }

    /// <summary>
    /// <see cref="PartialProfileJson"/> is a snapshot of whatever profile
    /// state the agent had assembled when the budget exhausted. Carried so
    /// the admin's review screen can render "here's what the agent had so
    /// far" without re-running extraction.
    /// </summary>
    public sealed record Escalated(string Reason, string PartialProfileJson) : ProviderState
    {
        [JsonIgnore]
        public override IntakeState Kind => IntakeState.Escalated;
    }
}
