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
    private readonly ConcurrentDictionary<string, string> _textCache = new();
    private readonly ConcurrentDictionary<string, byte[]> _bytesCache = new();

    public PromptLoader() : this(typeof(PromptLoader).Assembly) { }

    internal PromptLoader(Assembly assembly)
    {
        _assembly = assembly;
    }

    public async Task<string> LoadAsync(string promptPath, CancellationToken ct)
    {
        if (_textCache.TryGetValue(promptPath, out var cached))
            return cached;

        // Go through the byte path so the underlying read happens once even when
        // callers alternate between LoadAsync and LoadBytesAsync. The decoded
        // string lives alongside the bytes for fast subsequent reads.
        var bytes = await LoadBytesAsync(promptPath, ct);
        var text = Encoding.UTF8.GetString(bytes);
        _textCache[promptPath] = text;
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

    public async Task<byte[]> LoadBytesAsync(string promptPath, CancellationToken ct)
    {
        if (_bytesCache.TryGetValue(promptPath, out var cached))
            return cached;

        var resourceName = ResolveResourceName(promptPath);
        await using var stream = _assembly.GetManifestResourceStream(resourceName)
            ?? throw new PromptNotFoundException(promptPath);
        using var ms = new MemoryStream(capacity: (int)stream.Length);
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        _bytesCache[promptPath] = bytes;
        return bytes;
    }

    private string ResolveResourceName(string promptPath)
    {
        // Suffix-match is unique by construction iff no two source files share a leaf
        // name across the .csproj's EmbeddedResource roots. We have two roots today
        // (Prompts/** and Extraction/Prompts/**) and the audit trail depends on the
        // hash corresponding to *the* file — collisions must fail loud, not silently
        // pick whichever the loader iterates first.
        var suffix = "." + promptPath;
        var matches = _assembly.GetManifestResourceNames()
            .Where(r => r.EndsWith(suffix, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0) throw new PromptNotFoundException(promptPath);
        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"Prompt '{promptPath}' is ambiguous — multiple embedded resources match: " +
                string.Join(", ", matches) +
                ". Rename one or scope the lookup; the prompt_hash column depends on a unique resolution.");
        return matches[0];
    }
}
