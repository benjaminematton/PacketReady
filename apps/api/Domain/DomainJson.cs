using System.Text.Json;
using System.Text.Json.Serialization;

namespace PacketReady.Domain;

/// <summary>
/// Shared serializer options for JSONB-stored domain values
/// (<c>Provider.ProfileJson</c>, <c>ReadinessScore.IssuesJson</c>).
///
/// <para>Enums are written as <b>strings</b>, not ordinals, so a future rename or
/// reordering of an enum variant can never silently re-map existing rows. The
/// numeric pinning on <c>LicenseStatus</c>/<c>Severity</c>/etc. then becomes
/// belt-and-suspenders, not the load-bearing safeguard.</para>
///
/// <para>Unknown JSON members are <b>skipped</b>, not rejected. Phase 3 will reshape
/// <c>ProviderProfile</c> (rebuild from extraction rows) and add fields; Phase 1
/// readers must materialize new-shape rows without throwing. Without this flag,
/// the first added property would crash every reader still on the old binary.</para>
/// </summary>
public static class DomainJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        // CamelCase on the wire matches the ASP.NET Core default and the fixture
        // JSON files. Reads and writes use the same options, so round-tripping
        // through JSONB stays consistent.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };
}
