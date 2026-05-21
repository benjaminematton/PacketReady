using System.Text.Json.Serialization;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Seed;

/// <summary>
/// Wire shape of an <c>evals/fixtures/*.json</c> file. The <see cref="Profile"/> field
/// is a <see cref="ProviderProfile"/> with date placeholders pre-resolved.
/// <see cref="Notes"/> is informational arithmetic ("100 - 25 - 10 - 3 = 62"); declared
/// here so STJ validates the field name instead of silently dropping a typo via
/// <c>UnmappedMemberHandling.Skip</c>. Bound through <see cref="DomainJson.Options"/>,
/// so <see cref="ExpectedTier"/> deserializes via <c>JsonStringEnumConverter</c>.
/// </summary>
public sealed record FixtureModel(
    string Label,
    int ExpectedScore,
    Tier ExpectedTier,
    [property: JsonPropertyName("profile")] ProviderProfile Profile,
    string? Notes = null);
