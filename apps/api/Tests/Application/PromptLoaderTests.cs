using System.Reflection;
using System.Resources;
using PacketReady.Application.Prompts;
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
        // Use an in-memory test assembly with a fake embedded resource.
        var assembly = BuildAssemblyWithResource("FakePrompt.md", "Hello, {{name}}!");

        var loader = new TestablePromptLoader(assembly);
        var rendered = await loader.LoadAsync(
            "FakePrompt.md",
            new Dictionary<string, string> { ["name"] = "world" },
            CancellationToken.None);

        Assert.Equal("Hello, world!", rendered);
    }

    [Fact]
    public async Task LoadAsync_CachesRawText()
    {
        var assembly = BuildAssemblyWithResource("Cached.md", "raw text");
        var loader = new TestablePromptLoader(assembly);

        var a = await loader.LoadAsync("Cached.md", CancellationToken.None);
        var b = await loader.LoadAsync("Cached.md", CancellationToken.None);

        Assert.Same(a, b);
    }

    /// <summary>
    /// <see cref="PromptLoader"/> exposes an internal ctor; this subclass forwards
    /// without requiring InternalsVisibleTo on the production assembly.
    /// </summary>
    private sealed class TestablePromptLoader : IPromptLoader
    {
        private readonly PromptLoader _inner;

        public TestablePromptLoader(Assembly assembly)
        {
            // Reflection over the internal ctor — avoids leaking InternalsVisibleTo
            // just for tests. Constructor signature is stable enough to make this safe.
            var ctor = typeof(PromptLoader).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(Assembly)])!;
            _inner = (PromptLoader)ctor.Invoke([assembly]);
        }

        public Task<string> LoadAsync(string p, CancellationToken ct) => _inner.LoadAsync(p, ct);
        public Task<string> LoadAsync(string p, IReadOnlyDictionary<string, string> v, CancellationToken ct)
            => _inner.LoadAsync(p, v, ct);
    }

    private static Assembly BuildAssemblyWithResource(string name, string content)
    {
        // Reflection.Emit can't add manifest resources at run time; instead we use a
        // memory-backed StubAssembly that overrides GetManifestResourceStream.
        return new StubAssembly(name, content);
    }

    private sealed class StubAssembly : Assembly
    {
        private readonly string _name;
        private readonly byte[] _bytes;

        public StubAssembly(string name, string content)
        {
            _name = "PacketReady.Tests.Stub." + name;
            _bytes = System.Text.Encoding.UTF8.GetBytes(content);
        }

        public override string[] GetManifestResourceNames() => [_name];

        public override Stream? GetManifestResourceStream(string name)
            => name == _name ? new MemoryStream(_bytes, writable: false) : null;

        public override ManifestResourceInfo? GetManifestResourceInfo(string resourceName) => null;
    }
}
