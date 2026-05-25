using PacketReady.Application.Prompts;
using PacketReady.Tests.Application.Prompts;
using Xunit;

namespace PacketReady.Tests.Application;

public class PromptLoaderTests
{
    [Fact]
    public async Task LoadAsync_ThrowsForUnknownPrompt()
    {
        var loader = new PromptLoader();

        await Assert.ThrowsAsync<PromptNotFoundException>(
            () => loader.LoadAsync("definitely-not-a-real-prompt.md", CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_SubstitutesVariables()
    {
        var loader = StubPromptLoaderFactory.Create("FakePrompt.md", "Hello, {{name}}!");

        var rendered = await loader.LoadAsync(
            "FakePrompt.md",
            new Dictionary<string, string> { ["name"] = "world" },
            CancellationToken.None);

        Assert.Equal("Hello, world!", rendered);
    }

    [Fact]
    public async Task LoadAsync_CachesRawText()
    {
        var loader = StubPromptLoaderFactory.Create("Cached.md", "raw text");

        var a = await loader.LoadAsync("Cached.md", CancellationToken.None);
        var b = await loader.LoadAsync("Cached.md", CancellationToken.None);

        Assert.Same(a, b);
    }

    [Fact]
    public async Task LoadBytesAsync_ReturnsExactEmbeddedBytes()
    {
        // Round-trip must NOT pass through StreamReader — that would normalize
        // line endings and break the PromptHasher contract on Windows.
        var raw = "alpha\r\nbeta\r\n";
        var loader = StubPromptLoaderFactory.Create("Crlf.md", raw);

        var bytes = await loader.LoadBytesAsync("Crlf.md", CancellationToken.None);

        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(raw), bytes);
    }

    [Fact]
    public async Task LoadBytesAsync_CachesAcrossCalls()
    {
        var loader = StubPromptLoaderFactory.Create("CachedBytes.md", "stable");

        var a = await loader.LoadBytesAsync("CachedBytes.md", CancellationToken.None);
        var b = await loader.LoadBytesAsync("CachedBytes.md", CancellationToken.None);

        Assert.Same(a, b);
    }

    [Fact]
    public async Task LoadBytesAsync_ThrowsForUnknownPrompt()
    {
        var loader = new PromptLoader();

        await Assert.ThrowsAsync<PromptNotFoundException>(
            () => loader.LoadBytesAsync("definitely-not-a-real-prompt.md", CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenLeafNameMatchesMultipleResources()
    {
        // Two embedded resources end with the same leaf — simulates the .csproj
        // accidentally globbing the same filename out of both Prompts/** and
        // Extraction/Prompts/**. Without the multi-match guard, FirstOrDefault would
        // silently pick whichever the enumerator returned first and the prompt_hash
        // column would correspond to the wrong file.
        var assembly = new MultiStubPromptAssembly(
            ("PacketReady.Stub.Prompts.Duplicate.md", "from /Prompts"),
            ("PacketReady.Stub.Extraction.Prompts.Duplicate.md", "from /Extraction/Prompts"));
        var loader = StubPromptLoaderFactory.Create(assembly);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync("Duplicate.md", CancellationToken.None));

        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Prompts.Duplicate.md", ex.Message);
        Assert.Contains("Extraction.Prompts.Duplicate.md", ex.Message);
    }
}
