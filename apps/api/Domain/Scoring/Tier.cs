namespace PacketReady.Domain.Scoring;

/// <summary>
/// Categorical UI label for a <see cref="ReadinessScore"/>. Boundaries:
/// <list type="bullet">
///   <item><c>Green</c>: score ≥ 85</item>
///   <item><c>Yellow</c>: 60 ≤ score &lt; 85</item>
///   <item><c>Red</c>: score &lt; 60</item>
/// </list>
///
/// <para><b>Not a sort key.</b> <c>tier</c> is stored as TEXT in Postgres, so
/// <c>ORDER BY tier</c> sorts alphabetically (Green, Red, Yellow) — wrong. Use
/// <c>score</c> (numeric) when ordering: <c>ORDER BY score ASC</c> for worst-first.</para>
///
/// <para>Derived from score via <see cref="FromScore"/>; never accept a Tier from a
/// caller alongside a Score — the two would be free to disagree.</para>
/// </summary>
public enum Tier
{
    Red,
    Yellow,
    Green,
}

public static class TierExtensions
{
    /// <summary>Single source of truth for the score → tier mapping.</summary>
    public static Tier FromScore(int score) => score switch
    {
        >= 85 => Tier.Green,
        >= 60 => Tier.Yellow,
        _ => Tier.Red,
    };
}
