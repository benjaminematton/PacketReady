using System.Text.Json;

namespace PacketReady.Domain.Providers;

/// <summary>
/// Durable provider record. Phase 1 carries just <see cref="Id"/>, <see cref="CreatedAt"/>,
/// and the serialized <see cref="ProviderProfile"/>. Phase 5 will add <c>Email</c>,
/// intake-session FSM state, and outbound-message linkage.
///
/// <para>The profile lives as a JSONB blob — Phase 1 has no extraction layer to populate
/// structured columns, and Phase 3 will rebuild the profile from extraction rows rather
/// than promoting fields. Keeping the column shape stable across that transition.</para>
/// </summary>
public class Provider
{
    public Guid Id { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private string _profileJson = "{}";

    /// <summary>
    /// Cached deserialized profile. Not mapped — invalidated whenever the EF-driven
    /// <see cref="ProfileJson"/> setter reassigns. Domain entities are single-threaded
    /// per request (one DbContext, one scope), so no lock is needed.
    /// </summary>
    private ProviderProfile? _profileCache;

    /// <summary>
    /// JSON-serialized <see cref="ProviderProfile"/> using <see cref="DomainJson.Options"/>.
    /// Shape validated by <see cref="ProviderProfile.Validate"/> in <see cref="Create"/>.
    /// Default <c>"{}"</c> exists for EF materialization.
    /// </summary>
    public string ProfileJson
    {
        get => _profileJson;
        private set
        {
            _profileJson = value;
            _profileCache = null;
        }
    }

    private Provider() { }

    public static Provider Create(ProviderProfile profile, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var createdAt = now ?? DateTimeOffset.UtcNow;

        // Fail fast at the write boundary, not when a validator runs. A bad NPI or a
        // future DOB would otherwise surface as a misleading downstream Issue.
        // Defense-in-depth even when callers used ProviderProfile.Create: covers
        // `with`-mutated profiles and JSONB-deserialized profiles.
        ProviderProfile.Validate(profile, createdAt);

        return new Provider
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            ProfileJson = JsonSerializer.Serialize(profile, DomainJson.Options),
        };
    }

    /// <summary>
    /// Deserialized profile, cached after the first call. Cache invalidates if EF
    /// rehydrates the entity (the <see cref="ProfileJson"/> setter clears it).
    /// </summary>
    public ProviderProfile GetProfile() =>
        _profileCache ??= JsonSerializer.Deserialize<ProviderProfile>(_profileJson, DomainJson.Options)
        ?? throw new InvalidOperationException(
            $"Provider {Id} has invalid profile JSON; cannot deserialize.");
}
