using System.Text.Json;
using PacketReady.Domain.Providers;

namespace PacketReady.TuneIdentityCoherence;

/// <summary>
/// Builds a <see cref="ProviderProfile"/> from one packet's <c>golden.json</c>
/// without going through the aggregator or the DB. The CLI runs the LLM
/// validator on fixture data only — every per-doc field the validator reads
/// comes verbatim from the golden's <c>documents[].fields</c> blocks, so the
/// loader's job is just to pour those into the matching <c>*Info</c> sub-records.
///
/// <para>Top-level profile fields (fullName/dateOfBirth/npi/credentialingState)
/// are synthesized — they pass <see cref="ProviderProfile.Validate"/> against
/// a pinned <c>nowUtc</c>, but the validator under test doesn't read them.
/// Don't use the resulting profile for anything other than feeding the
/// IdentityCoherence validator.</para>
/// </summary>
public static class PacketGoldenLoader
{
    // Matches packets.py's _NEW_PACKET_ANCHOR. Dates on the goldens are pinned
    // to this anchor so the synthesized profile's Validate() call uses the same
    // reference and the goldens don't tip into "expired" mid-tuning.
    public static readonly DateTimeOffset Anchor =
        new(2026, 5, 25, 0, 0, 0, TimeSpan.Zero);

    public static ProviderProfile LoadProfile(string packetDir)
    {
        var goldenPath = Path.Combine(packetDir, "golden.json");
        if (!File.Exists(goldenPath))
            throw new FileNotFoundException(
                $"golden.json not found in {packetDir}. Did you regen the dataset?",
                goldenPath);

        using var doc = JsonDocument.Parse(File.ReadAllText(goldenPath));
        var root = doc.RootElement;
        var documents = root.GetProperty("documents");

        LicenseInfo? license = null;
        DeaInfo? dea = null;
        BoardCertInfo? board = null;

        foreach (var d in documents.EnumerateArray())
        {
            var type = d.GetProperty("type").GetString();
            var fields = d.GetProperty("fields");
            switch (type)
            {
                case "license":     license = BuildLicense(fields); break;
                case "dea":         dea     = BuildDea(fields);     break;
                case "boardCert":   board   = BuildBoardCert(fields); break;
                // malpractice: ProviderProfile doesn't carry a sub-record for
                // it yet (lands with the malpractice currency validator in
                // task 11). The fullName on malpractice.fields is the
                // load-bearing value for malpractice-side identity coherence,
                // but until the sub-record exists, IdentityCoherence reads
                // only the three present sources.
                case "malpractice": break;
            }
        }

        // Synthesized top-level fields that pass shape validation. The
        // validator under test ignores all four; bumping these values
        // wouldn't change any tuning iteration's output.
        var canonicalFullName = license?.FullName ?? "Synthesized Fixture, MD";
        var state = license?.State ?? "NY";

        return ProviderProfile.Create(
            fullName: canonicalFullName,
            dateOfBirth: new DateOnly(1980, 1, 1),
            npi: "1234567890",
            credentialingState: state,
            nowUtc: Anchor,
            license: license,
            dea: dea,
            boardCert: board);
    }

    private static LicenseInfo? BuildLicense(JsonElement fields)
    {
        var number = StringOrNull(fields, "licenseNumber");
        var state = StringOrNull(fields, "state");
        var issue = DateOnlyOrNull(fields, "issueDate");
        var expiry = DateOnlyOrNull(fields, "expiryDate");
        var status = ParseEnum<LicenseStatus>(StringOrNull(fields, "status"));
        var fullName = StringOrNull(fields, "fullName") ?? "";
        if (number is null || state is null || issue is null || expiry is null) return null;
        return new LicenseInfo(number, state, issue.Value, expiry.Value, status, fullName);
    }

    private static DeaInfo? BuildDea(JsonElement fields)
    {
        var number = StringOrNull(fields, "deaNumber");
        var expiry = DateOnlyOrNull(fields, "expiryDate");
        var status = ParseEnum<DeaStatus>(StringOrNull(fields, "status"));
        var fullName = StringOrNull(fields, "fullName") ?? "";
        var schedules = new List<DeaSchedule>();
        if (fields.TryGetProperty("schedules", out var schedEl)
            && schedEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in schedEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && Enum.TryParse<DeaSchedule>(item.GetString(), out var s))
                    schedules.Add(s);
            }
        }
        if (number is null || expiry is null) return null;
        return new DeaInfo(number, expiry.Value, status, schedules, fullName);
    }

    private static BoardCertInfo? BuildBoardCert(JsonElement fields)
    {
        var board = StringOrNull(fields, "board");
        var specialty = StringOrNull(fields, "specialty");
        var issue = DateOnlyOrNull(fields, "issueDate");
        var expiry = DateOnlyOrNull(fields, "expiryDate");
        var status = ParseEnum<BoardCertStatus>(StringOrNull(fields, "status"));
        var fullName = StringOrNull(fields, "fullName") ?? "";
        if (board is null || specialty is null || issue is null || expiry is null) return null;
        return new BoardCertInfo(board, specialty, issue.Value, expiry.Value, status, fullName);
    }

    private static string? StringOrNull(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static DateOnly? DateOnlyOrNull(JsonElement obj, string key)
    {
        var s = StringOrNull(obj, key);
        return s is not null && DateOnly.TryParse(s, out var d) ? d : null;
    }

    private static T ParseEnum<T>(string? s) where T : struct, Enum =>
        s is not null && Enum.TryParse<T>(s, ignoreCase: true, out var v) ? v : default;

    /// <summary>
    /// Pulls the planted-conflict markers from a packet's golden.json.
    /// The CLI uses these to decide whether a validator emission is a TP
    /// (matches a planted shape with expected_to_flag=true), an FP on a
    /// clean packet, or an FP on a don't-flag planted marker.
    /// </summary>
    public static IReadOnlyList<PlantedMarker> LoadPlantedConflicts(string packetDir)
    {
        var goldenPath = Path.Combine(packetDir, "golden.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(goldenPath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("plantedConflicts", out var markers)
            || markers.ValueKind != JsonValueKind.Array)
            return Array.Empty<PlantedMarker>();

        var list = new List<PlantedMarker>();
        foreach (var m in markers.EnumerateArray())
        {
            list.Add(new PlantedMarker(
                Kind: m.GetProperty("kind").GetString() ?? "",
                Shape: m.TryGetProperty("shape", out var sh) ? sh.GetString() : null,
                ExpectedToFlag: m.TryGetProperty("expected_to_flag", out var ef) && ef.GetBoolean(),
                Description: m.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ""));
        }
        return list;
    }
}

/// <summary>One row of a packet's plantedConflicts array, parsed for the CLI.</summary>
public sealed record PlantedMarker(
    string Kind,
    string? Shape,
    bool ExpectedToFlag,
    string Description);
