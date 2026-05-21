using PacketReady.Domain.Scoring;
using Xunit;

namespace PacketReady.Tests.Domain.Scoring;

public sealed class TierExtensionsTests
{
    [Theory]
    [InlineData(100, Tier.Green)]
    [InlineData(85, Tier.Green)]      // boundary: ≥85 is Green
    [InlineData(84, Tier.Yellow)]     // boundary: <85 falls into Yellow
    [InlineData(70, Tier.Yellow)]
    [InlineData(60, Tier.Yellow)]     // boundary: ≥60 is Yellow
    [InlineData(59, Tier.Red)]        // boundary: <60 falls into Red
    [InlineData(34, Tier.Red)]
    [InlineData(0, Tier.Red)]
    public void FromScore_RespectsBoundaries(int score, Tier expected)
    {
        Assert.Equal(expected, TierExtensions.FromScore(score));
    }
}
