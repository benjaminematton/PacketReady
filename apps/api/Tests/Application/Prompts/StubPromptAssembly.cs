using System.Reflection;
using PacketReady.Application.Prompts;

namespace PacketReady.Tests.Application.Prompts;

/// <summary>
/// In-memory single-resource <see cref="Assembly"/> backing <see cref="PromptLoader"/> in
/// unit tests. Reflection.Emit can't add manifest resources at runtime, so the loader's
/// internal ctor takes any <see cref="Assembly"/> and we override the two methods it
/// uses: <see cref="GetManifestResourceNames"/> and <see cref="GetManifestResourceStream(string)"/>.
/// </summary>
internal sealed class StubPromptAssembly : Assembly
{
    private readonly string _resourceName;
    private readonly byte[] _bytes;

    public StubPromptAssembly(string promptPath, string content)
    {
        // Use a namespaced resource name so the loader's leading-dot suffix match
        // ("." + promptPath) finds it the same way it would find a real embedded
        // resource — e.g. "PacketReady.Tests.Stub.Foo.md" matches ".Foo.md".
        _resourceName = "PacketReady.Tests.Stub." + promptPath;
        _bytes = System.Text.Encoding.UTF8.GetBytes(content);
    }

    public override string[] GetManifestResourceNames() => [_resourceName];

    public override Stream? GetManifestResourceStream(string name)
        => name == _resourceName ? new MemoryStream(_bytes, writable: false) : null;

    public override ManifestResourceInfo? GetManifestResourceInfo(string resourceName) => null;
}

/// <summary>
/// Multi-resource variant — used by the ambiguity-collision test, where two embedded
/// resources share the same leaf name and the loader must refuse to guess.
/// </summary>
internal sealed class MultiStubPromptAssembly : Assembly
{
    private readonly Dictionary<string, byte[]> _resources;

    public MultiStubPromptAssembly(params (string FullResourceName, string Content)[] resources)
    {
        _resources = resources.ToDictionary(
            r => r.FullResourceName,
            r => System.Text.Encoding.UTF8.GetBytes(r.Content));
    }

    public override string[] GetManifestResourceNames() => _resources.Keys.ToArray();

    public override Stream? GetManifestResourceStream(string name)
        => _resources.TryGetValue(name, out var bytes) ? new MemoryStream(bytes, writable: false) : null;

    public override ManifestResourceInfo? GetManifestResourceInfo(string resourceName) => null;
}

/// <summary>
/// Builds a real <see cref="PromptLoader"/> against an in-memory assembly via its
/// internal ctor. Reflection here avoids leaking InternalsVisibleTo onto the
/// production assembly just for tests.
/// </summary>
internal static class StubPromptLoaderFactory
{
    public static IPromptLoader Create(string promptPath, string content)
        => InvokeCtor(new StubPromptAssembly(promptPath, content));

    public static IPromptLoader Create(Assembly assembly) => InvokeCtor(assembly);

    private static IPromptLoader InvokeCtor(Assembly assembly)
    {
        var ctor = typeof(PromptLoader).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(Assembly)])!;
        return (PromptLoader)ctor.Invoke([assembly]);
    }
}
