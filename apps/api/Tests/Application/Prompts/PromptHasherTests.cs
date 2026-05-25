using PacketReady.Application.Prompts;
using Xunit;

namespace PacketReady.Tests.Application.Prompts;

public class PromptHasherTests
{
    private const string HelloSha256 = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

    [Fact]
    public async Task HashOfAsync_ProducesKnownSha256()
    {
        // Test vector: SHA-256("hello") = the constant above. Pinning the well-known
        // hash guards against any implementation drift (algorithm swap, encoding
        // confusion, hex casing) — the column on every extraction row depends on
        // this being deterministic.
        var loader = StubPromptLoaderFactory.Create("Hello.md", "hello");
        var hasher = new PromptHasher(loader);

        var hex = await hasher.HashOfAsync("Hello.md", CancellationToken.None);

        Assert.Equal(HelloSha256, hex);
    }

    [Fact]
    public async Task HashOfAsync_IsLowercaseHex()
    {
        var loader = StubPromptLoaderFactory.Create("Hex.md", "hello");
        var hasher = new PromptHasher(loader);

        var hex = await hasher.HashOfAsync("Hex.md", CancellationToken.None);

        Assert.Equal(64, hex.Length);
        Assert.Equal(hex, hex.ToLowerInvariant());
    }

    [Fact]
    public async Task HashOfAsync_CachesAcrossCalls()
    {
        var loader = StubPromptLoaderFactory.Create("Cached.md", "stable input");
        var hasher = new PromptHasher(loader);

        var a = await hasher.HashOfAsync("Cached.md", CancellationToken.None);
        var b = await hasher.HashOfAsync("Cached.md", CancellationToken.None);

        // Caching is a perf nicety; the contract is the hash, not identity. Assert
        // string-equal rather than ReferenceEquals so the test survives any future
        // intern/no-intern micro-optimization in PromptHasher.
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task HashOfAsync_DiffersOnLineEndingDifference()
    {
        // The whole point of going through bytes (not strings) is that LF vs CRLF
        // produces a different hash. If this test ever passes both ways, line-ending
        // normalization has crept back in and the .gitattributes pin is no longer
        // load-bearing.
        var lf = new PromptHasher(StubPromptLoaderFactory.Create("LF.md", "a\nb\n"));
        var crlf = new PromptHasher(StubPromptLoaderFactory.Create("CRLF.md", "a\r\nb\r\n"));

        var lfHash = await lf.HashOfAsync("LF.md", CancellationToken.None);
        var crlfHash = await crlf.HashOfAsync("CRLF.md", CancellationToken.None);

        Assert.NotEqual(lfHash, crlfHash);
    }

    [Fact]
    public async Task HashOfAsync_PropagatesPromptNotFound()
    {
        var hasher = new PromptHasher(new PromptLoader());
        await Assert.ThrowsAsync<PromptNotFoundException>(
            () => hasher.HashOfAsync("not-a-prompt.md", CancellationToken.None));
    }
}
