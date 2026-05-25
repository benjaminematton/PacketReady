using PacketReady.Application.Prompts;
using Xunit;

namespace PacketReady.Tests.Application.Prompts;

/// <summary>
/// Guard for the <c>EmbeddedResource</c> glob in <c>Application.csproj</c>. If a
/// future refactor moves the prompts dir or drops the glob, every shipped P3
/// prompt fails to resolve here, not at first inbound request in prod.
///
/// <para>Hash stability for these same prompts is enforced by
/// <c>PinnedShippedPromptHashesTests</c> — that's a stronger check than
/// determinism-over-two-calls, so it lives there, not here.</para>
/// </summary>
public class ShippedPromptsResolveTests
{
    public static IEnumerable<object[]> AllPromptKeys => new[]
    {
        new object[] { PromptKeys.Classifier },
        new object[] { PromptKeys.LicenseExtraction },
        new object[] { PromptKeys.DeaExtraction },
        new object[] { PromptKeys.BoardCertExtraction },
        new object[] { PromptKeys.MalpracticeExtraction },
    };

    [Theory]
    [MemberData(nameof(AllPromptKeys))]
    public async Task EveryShippedPromptLoadsViaPromptLoader(string promptKey)
    {
        var loader = new PromptLoader();
        var text = await loader.LoadAsync(promptKey, CancellationToken.None);

        Assert.NotNull(text);
        Assert.NotEmpty(text);
    }
}
