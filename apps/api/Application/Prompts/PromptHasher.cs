using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PacketReady.Application.Prompts;

/// <summary>
/// SHA-256 of a prompt's embedded-resource bytes, lowercase hex. The hash lands on
/// every <c>documents.classifier_prompt_hash</c> and <c>document_extractions.prompt_hash</c>
/// column; it's the audit anchor that says "this row was produced by exactly this
/// prompt file." Editing a <c>v1.md</c> after rows have been written breaks the
/// audit trail silently — the spec calls for promoting to <c>v2.md</c> instead.
///
/// <para>Resolution goes through <see cref="IPromptLoader.LoadBytesAsync"/>, not
/// the string overload — UTF-8 round-trip + line-ending normalization would change
/// the hash on a Windows commit. Bytes are stable, strings are not.</para>
///
/// <para>Hashes cache per prompt path; the embedded bytes don't change at runtime,
/// so a single hash computation per process is enough.</para>
/// </summary>
public sealed class PromptHasher
{
    private readonly IPromptLoader _prompts;
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public PromptHasher(IPromptLoader prompts)
    {
        _prompts = prompts;
    }

    /// <summary>
    /// Lowercase-hex SHA-256 of the embedded bytes for <paramref name="promptPath"/>.
    /// Throws <see cref="PromptNotFoundException"/> when the resource is missing.
    /// </summary>
    public async Task<string> HashOfAsync(string promptPath, CancellationToken ct)
    {
        if (_cache.TryGetValue(promptPath, out var cached))
            return cached;

        var bytes = await _prompts.LoadBytesAsync(promptPath, ct);
        var hash = SHA256.HashData(bytes);
        var hex = Convert.ToHexStringLower(hash);

        _cache[promptPath] = hex;
        return hex;
    }
}
