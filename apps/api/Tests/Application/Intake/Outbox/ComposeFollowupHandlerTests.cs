using PacketReady.Application.Intake.Outbox;
using Xunit;

namespace PacketReady.Tests.Application.Intake.Outbox;

public class ComposeFollowupHandlerTests
{
    private readonly ComposeFollowupHandler _handler = new();

    [Fact]
    public void Compose_SingleGap_UsesSingularSubjectAndBody()
    {
        var followup = _handler.Compose("Henry", new[]
        {
            new ComposeFollowupHandler.Gap("missing_dea", "We don't have your DEA on file yet."),
        });

        Assert.Equal("PacketReady — one more thing to wrap your intake", followup.Subject);
        Assert.Contains("Hi Henry", followup.Body);
        Assert.Contains("There's one item left", followup.Body);
        Assert.Contains("1. We don't have your DEA on file yet.", followup.Body);
    }

    [Fact]
    public void Compose_MultipleGaps_UsesPluralSubjectWithCount()
    {
        var followup = _handler.Compose("Jane", new[]
        {
            new ComposeFollowupHandler.Gap("missing_dea", "DEA is missing."),
            new ComposeFollowupHandler.Gap("expired_license", "License expired last month."),
            new ComposeFollowupHandler.Gap("low_conf_dob", "DOB is hard to read on your license."),
        });

        Assert.Equal("PacketReady — 3 items left on your intake", followup.Subject);
        Assert.Contains("There are 3 items left", followup.Body);
        Assert.Contains("Hi Jane", followup.Body);
    }

    [Fact]
    public void Compose_AddsRemediationHintInParentheses()
    {
        var followup = _handler.Compose("Henry", new[]
        {
            new ComposeFollowupHandler.Gap(
                "missing_malpractice",
                "We need your malpractice declaration.",
                "Upload the declaration page, not the cover letter."),
        });

        Assert.Contains("(Upload the declaration page, not the cover letter.)", followup.Body);
    }

    [Fact]
    public void Compose_OmitsHintWhenAbsent()
    {
        var followup = _handler.Compose("Henry", new[]
        {
            new ComposeFollowupHandler.Gap("missing_dea", "DEA missing."),
        });

        Assert.DoesNotContain("(", followup.Body);
    }

    [Fact]
    public void Compose_SortsGapsByKindForDeterminism()
    {
        // Two gap lists with the same contents in different orders must
        // produce the same body. Lets replays + telemetry diffs work.
        var a = _handler.Compose("X", new[]
        {
            new ComposeFollowupHandler.Gap("z_late", "late item"),
            new ComposeFollowupHandler.Gap("a_early", "early item"),
        });
        var b = _handler.Compose("X", new[]
        {
            new ComposeFollowupHandler.Gap("a_early", "early item"),
            new ComposeFollowupHandler.Gap("z_late", "late item"),
        });

        Assert.Equal(a.Body, b.Body);
        // And the sort key ('a_' before 'z_') determines which renders as 1.
        Assert.Contains("1. early item", a.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Compose_FallsBackToThereWhenNameBlank(string? name)
    {
        var followup = _handler.Compose(name!, new[]
        {
            new ComposeFollowupHandler.Gap("missing_dea", "DEA missing."),
        });

        Assert.Contains("Hi there,", followup.Body);
    }

    [Fact]
    public void Compose_RejectsEmptyGapList()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => _handler.Compose("Henry", Array.Empty<ComposeFollowupHandler.Gap>()));
        Assert.Equal("gaps", ex.ParamName);
    }
}
