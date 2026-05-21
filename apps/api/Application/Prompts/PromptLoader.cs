using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace PacketReady.Application.Prompts;

/// <summary>
/// Default <see cref="IPromptLoader"/>. Reads embedded .md resources from the
/// Application assembly. Caches raw text on first read; substitution re-runs per call
/// (cheap since templates are KB-sized).
///
/// <para>Resource lookup is name-suffix matching with a leading dot:
/// <c>FooPrompt.md</c> matches the resource ending in <c>.FooPrompt.md</c>. The
/// leading-dot requirement prevents <c>OtherFooPrompt.md</c> from accidentally
/// matching <c>FooPrompt.md</c>.</para>
/// </summary>
public sealed class PromptLoader : IPromptLoader
{
    private readonly Assembly _assembly;
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public PromptLoader() : this(typeof(PromptLoader).Assembly) { }

    internal PromptLoader(Assembly assembly)
    {
        _assembly = assembly;
    }

    public async Task<string> LoadAsync(string promptPath, CancellationToken ct)
    {
        if (_cache.TryGetValue(promptPath, out var cached))
            return cached;

        var resourceName = ResolveResourceName(promptPath);
        await using var stream = _assembly.GetManifestResourceStream(resourceName)
            ?? throw new PromptNotFoundException(promptPath);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct);

        _cache[promptPath] = text;
        return text;
    }

    public async Task<string> LoadAsync(
        string promptPath,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken ct)
    {
        var raw = await LoadAsync(promptPath, ct);
        if (variables.Count == 0) return raw;

        var sb = new StringBuilder(raw);
        foreach (var (key, value) in variables)
            sb.Replace($"{{{{{key}}}}}", value);
        return sb.ToString();
    }

    private string ResolveResourceName(string promptPath)
    {
        var suffix = "." + promptPath;
        var resources = _assembly.GetManifestResourceNames();
        var match = resources.FirstOrDefault(r => r.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new PromptNotFoundException(promptPath);
        return match;
    }
}
