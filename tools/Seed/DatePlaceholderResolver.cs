using System.Text.RegularExpressions;

namespace PacketReady.Seed;

/// <summary>
/// Resolves <c>{today±Nd|w|y}</c> → ISO date string and <c>{now±Nd|w|y}</c> → ISO 8601
/// timestamp string inside a fixture JSON blob. Two forms because <c>DateOnly</c> and
/// <c>DateTimeOffset</c> parse differently in STJ — a single form would have to know
/// the target type at substitution time. Bare <c>{today}</c>/<c>{now}</c> are accepted
/// and resolve to the anchor with no offset.
/// </summary>
public static class DatePlaceholderResolver
{
    private static readonly Regex Pattern =
        new(@"\{(today|now)(?:([+-])(\d+)([dwy]))?\}", RegexOptions.Compiled);

    public static string Resolve(string raw, DateTimeOffset nowUtc)
    {
        var today = DateOnly.FromDateTime(nowUtc.UtcDateTime);

        return Pattern.Replace(raw, m =>
        {
            var anchor = m.Groups[1].Value;        // "today" | "now"
            var hasOffset = m.Groups[2].Success;
            var sign = hasOffset ? m.Groups[2].Value : "+";
            var magnitude = hasOffset ? int.Parse(m.Groups[3].Value) : 0;
            var unit = hasOffset ? m.Groups[4].Value : "d";

            var signedMagnitude = sign == "-" ? -magnitude : magnitude;

            if (anchor == "today")
            {
                var d = unit switch
                {
                    "d" => today.AddDays(signedMagnitude),
                    "w" => today.AddDays(signedMagnitude * 7),
                    "y" => today.AddYears(signedMagnitude),
                    _ => throw new InvalidOperationException($"Unknown unit '{unit}' in token '{m.Value}'"),
                };
                return d.ToString("yyyy-MM-dd");
            }
            else // "now"
            {
                var dt = unit switch
                {
                    "d" => nowUtc.AddDays(signedMagnitude),
                    "w" => nowUtc.AddDays(signedMagnitude * 7),
                    "y" => nowUtc.AddYears(signedMagnitude),
                    _ => throw new InvalidOperationException($"Unknown unit '{unit}' in token '{m.Value}'"),
                };
                return dt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
            }
        });
    }
}
