using System.Globalization;
using PacketReady.Application.Nucc;

namespace PacketReady.Infrastructure.Nucc;

/// <summary>
/// CSV-backed <see cref="INuccTaxonomyLookup"/>. Loads the entire NUCC
/// snapshot into a frozen Dictionary at construction; subsequent
/// <see cref="TryGet"/> calls are O(1).
///
/// <para>Parser is hand-rolled — the NUCC CSV's <c>Definition</c> column
/// contains embedded commas and quotes, but the row count is small
/// (~900 rows × ~1KB each ≈ 1MB) and well-formed; a real CSV library
/// (CsvHelper) would add a dependency for a one-shot file read at
/// startup. The minimal RFC-4180 implementation here handles quoted
/// fields and escaped quotes (<c>""</c>), which is all the snapshot
/// uses. Fail-loud on a row that doesn't have at least the 7
/// header-implied columns — schema drift surfaces at startup, not
/// mid-request.</para>
///
/// <para>The canonical specialty is the <c>Display Name</c> column —
/// NUCC's official human-readable label. <c>Specialization</c> and
/// <c>Classification</c> are intermediate hierarchy levels that
/// don't always agree with the practitioner's printed specialty
/// (a card-printed "Cardiology" maps to the Display Name
/// "Cardiovascular Disease Physician"), so the LLM compare step
/// gets the Display Name and lets synonymy fall to its judgment.</para>
/// </summary>
public sealed class NuccTaxonomyLookup : INuccTaxonomyLookup
{
    private const string CodeColumn = "Code";
    private const string DisplayNameColumn = "Display Name";

    private readonly IReadOnlyDictionary<string, string> _byCode;

    public NuccTaxonomyLookup(string csvPath)
    {
        _byCode = Load(csvPath);
    }

    public bool TryGet(string taxonomyCode, out string canonicalSpecialty)
    {
        // NUCC codes are 10-character alphanumeric ending in X. We don't
        // validate shape here — a malformed code is just a guaranteed miss.
        if (string.IsNullOrWhiteSpace(taxonomyCode))
        {
            canonicalSpecialty = "";
            return false;
        }
        if (_byCode.TryGetValue(taxonomyCode, out var v))
        {
            canonicalSpecialty = v;
            return true;
        }
        canonicalSpecialty = "";
        return false;
    }

    public int Count => _byCode.Count;

    private static IReadOnlyDictionary<string, string> Load(string csvPath)
    {
        if (!File.Exists(csvPath))
            throw new InvalidOperationException(
                $"NUCC taxonomy CSV not found at '{csvPath}'. Expected the snapshot " +
                $"under data/nucc-taxonomy-XX.X.csv, copied to the assembly's " +
                $"Nucc\\ folder via Infrastructure.csproj <Content Include>. " +
                $"If you bumped the snapshot version, update the path constant in DI.");

        using var reader = new StreamReader(csvPath);
        var header = reader.ReadLine()
            ?? throw new InvalidOperationException(
                $"NUCC CSV '{csvPath}' is empty (no header row).");

        var columns = ParseCsvRow(header);
        var codeIdx = Array.IndexOf(columns, CodeColumn);
        var nameIdx = Array.IndexOf(columns, DisplayNameColumn);
        if (codeIdx < 0 || nameIdx < 0)
            throw new InvalidOperationException(
                $"NUCC CSV header missing required columns. Need '{CodeColumn}' and " +
                $"'{DisplayNameColumn}'; got [{string.Join(", ", columns)}].");

        var map = new Dictionary<string, string>(capacity: 1000, StringComparer.Ordinal);
        string? line;
        var lineNo = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseCsvRow(line);
            if (fields.Length <= Math.Max(codeIdx, nameIdx))
                throw new InvalidOperationException(
                    $"NUCC CSV row {lineNo} has {fields.Length} fields; need at least " +
                    $"{Math.Max(codeIdx, nameIdx) + 1}. Row: '{line}'.");

            var code = fields[codeIdx].Trim();
            var displayName = fields[nameIdx].Trim();
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(displayName)) continue;
            // Last write wins on duplicate codes — NUCC snapshots don't
            // contain duplicates in practice; if they ever do, the operator
            // can spot it via Count mismatch in startup logs.
            map[code] = displayName;
        }

        if (map.Count == 0)
            throw new InvalidOperationException(
                $"NUCC CSV '{csvPath}' parsed to zero rows. Check the file's encoding " +
                $"and that the header columns match the snapshot's shape.");

        return map;
    }

    /// <summary>
    /// Minimal RFC-4180 CSV row parser: handles quoted fields, embedded
    /// commas inside quotes, and escaped quotes (<c>""</c>). Does not
    /// handle multi-line quoted values, which the NUCC snapshot doesn't
    /// use. Throws nothing — malformed rows surface downstream as missing
    /// columns, where the loader's count check fails loud.
    /// </summary>
    private static string[] ParseCsvRow(string row)
    {
        var fields = new List<string>(capacity: 8);
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < row.Length; i++)
        {
            var c = row[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    // Doubled quote inside quoted field is the escape for a
                    // literal quote. Otherwise this is the closing quote.
                    if (i + 1 < row.Length && row[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == ',')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}
