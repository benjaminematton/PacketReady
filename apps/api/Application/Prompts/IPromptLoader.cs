namespace PacketReady.Application.Prompts;

/// <summary>
/// Loads prompt templates from embedded resources. Two overloads: raw text vs
/// <c>{{key}}</c>-substituted. Ported from VaBene's IPromptLoader without semantic change.
///
/// <para><see cref="PromptKeys"/> below holds the canonical prompt resource paths so
/// callers don't string-literal them. Mismatch fails fast at load-time with
/// <see cref="PromptNotFoundException"/>.</para>
///
/// <para>Phase 0: no embedded prompts yet — <see cref="PromptKeys"/> is empty and
/// <c>PromptResourceValidator</c> passes a no-op startup check. Phase 3 adds the first
/// extractor prompts.</para>
/// </summary>
public interface IPromptLoader
{
    Task<string> LoadAsync(string promptPath, CancellationToken ct);

    Task<string> LoadAsync(
        string promptPath,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken ct);
}

/// <summary>
/// Canonical resource paths for prompts. Each constant value is a filename matching an
/// <c>EmbeddedResource</c> under <c>Application/Prompts/</c>. Empty in Phase 0; phases
/// add entries as their first LLM call lands.
/// </summary>
public static class PromptKeys
{
    // Phase 3 will add license / dea / malpractice / board_cert / cv extraction prompts.
    // Phase 4 will add identity_coherence and npi_taxonomy_match validator prompts.
}

public sealed class PromptNotFoundException : Exception
{
    public string PromptPath { get; }

    public PromptNotFoundException(string promptPath)
        : base($"Prompt resource '{promptPath}' not found in the Application assembly. " +
               "Confirm the file exists under Application/Prompts/ and the .csproj's " +
               "<EmbeddedResource> glob covers it.")
    {
        PromptPath = promptPath;
    }
}
