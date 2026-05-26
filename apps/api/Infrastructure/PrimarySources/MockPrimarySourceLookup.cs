using System.Text.Json;
using PacketReady.Application.Intake.PrimarySources;

namespace PacketReady.Infrastructure.PrimarySources;

/// <summary>
/// 5-entry canned lookup table for the P5 demo loop. Keyed by
/// <c>(source, npi)</c> — the only identifier shape any source currently
/// uses in our demo data. Real CAQH/NPPES/OIG/SAM/state-board integration
/// is post-launch (phase-5-intake-agent.md "Out of scope").
///
/// <para>The 5 entries cover the 3 demo providers (green / yellow / red
/// readiness tiers) plus 2 edge cases that exercise the mismatch and
/// not-found branches of the agent's reasoning loop:</para>
/// <list type="bullet">
///   <item><b>NPI 1234567890</b> → clean NPPES match (green-tier henry).</item>
///   <item><b>NPI 9876543210</b> → NPPES match with a name mismatch
///         (yellow-tier; agent should flag and ask for clarification).</item>
///   <item><b>NPI 5555555555</b> → OIG hit (red-tier sanctioned).</item>
///   <item><b>NPI 1111111111</b> → state board mismatch (license expired
///         per state of record; tests the cross-source-conflict path).</item>
///   <item><b>NPI 0000000000</b> → not found in any source (tests the
///         all-misses branch).</item>
/// </list>
/// </summary>
public sealed class MockPrimarySourceLookup : IPrimarySourceLookup
{
    // (source, npi) → canned response. Static so the dictionary is built
    // once per process — the lookup is hot during the agent loop.
    private static readonly Dictionary<(string Source, string Npi), object> _table = new()
    {
        // — Green-tier provider (henry anderson, fully clean) ——
        [("nppes", "1234567890")] = new
        {
            found = true,
            fields = new
            {
                full_name = "Henry Anderson",
                npi = "1234567890",
                taxonomy_code = "207R00000X",   // internal medicine
                state = "CA",
            },
            mismatch_fields = Array.Empty<string>(),
        },
        [("oig", "1234567890")] = new
        {
            found = false,                       // clean — no OIG sanction
            fields = (object?)null,
            mismatch_fields = Array.Empty<string>(),
        },
        [("sam", "1234567890")] = new
        {
            found = false,                       // clean — no SAM exclusion
            fields = (object?)null,
            mismatch_fields = Array.Empty<string>(),
        },
        [("state_board", "1234567890")] = new
        {
            found = true,
            fields = new
            {
                license_status = "active",
                license_number = "G123456",
                state = "CA",
                expiry_date = "2028-06-30",
            },
            mismatch_fields = Array.Empty<string>(),
        },

        // — Yellow-tier provider (name mismatch on NPPES) ——
        [("nppes", "9876543210")] = new
        {
            found = true,
            fields = new
            {
                full_name = "Jonathan Smith",   // provider gave "John Smith Jr." on uploads
                npi = "9876543210",
                taxonomy_code = "208000000X",   // pediatrics
                state = "CA",
            },
            mismatch_fields = new[] { "full_name" },
        },

        // — Red-tier provider (OIG hit) ——
        [("oig", "5555555555")] = new
        {
            found = true,                        // sanction present
            fields = new
            {
                full_name = "Sample Sanctioned MD",
                exclusion_date = "2024-03-15",
                exclusion_type = "Mandatory",
            },
            mismatch_fields = Array.Empty<string>(),
        },

        // — Cross-source conflict edge case ——
        [("state_board", "1111111111")] = new
        {
            found = true,
            fields = new
            {
                license_status = "expired",
                license_number = "G999999",
                state = "CA",
                expiry_date = "2022-01-31",
            },
            mismatch_fields = new[] { "license_status", "expiry_date" },
        },

        // — All-misses edge case ——
        // NPI 0000000000 not in any source — the agent should see
        // { found: false } from every lookup and reason about it as
        // "this provider can't be primary-source-verified."
    };

    public Task<JsonElement> LookupAsync(
        string source,
        JsonElement identifiers,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
            return Task.FromResult(NotFound("source is required."));

        if (identifiers.ValueKind != JsonValueKind.Object
            || !identifiers.TryGetProperty("npi", out var npiEl)
            || npiEl.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(NotFound("identifiers must include an 'npi' string."));
        }

        var npi = npiEl.GetString()!;
        var canonical = source.ToLowerInvariant().Trim();

        if (_table.TryGetValue((canonical, npi), out var entry))
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
            return Task.FromResult(JsonDocument.Parse(bytes).RootElement);
        }

        // Unknown source name OR npi-not-in-table → not_found. This is
        // the most common branch the agent hits in the demo — every
        // (source, npi) pair we don't seed lands here.
        return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(new
        {
            found = false,
            fields = (object?)null,
            mismatch_fields = Array.Empty<string>(),
        }) is var b ? JsonDocument.Parse(b).RootElement : default);
    }

    private static JsonElement NotFound(string error)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { found = false, error });
        return JsonDocument.Parse(bytes).RootElement;
    }
}
