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

    /// <summary>
    /// Raw embedded-resource bytes. Used by <c>PromptHasher</c> to compute the
    /// SHA-256 that lands on every extraction row — line-ending normalization or
    /// UTF-8 decoding would change the hash silently, so callers go through
    /// bytes, not strings.
    /// </summary>
    Task<byte[]> LoadBytesAsync(string promptPath, CancellationToken ct);
}

/// <summary>
/// Canonical resource paths for prompts. Each constant value is a filename matching an
/// <c>EmbeddedResource</c> under <c>Application/Prompts/</c> or
/// <c>Application/Extraction/Prompts/</c>. Phase 0: empty; Phase 3 adds the
/// classifier + four extractor prompts below; Phase 4 will add identity_coherence
/// and npi_taxonomy_match validator prompts.
/// </summary>
public static class PromptKeys
{
    // Phase 3 — document classification + per-doc-type field extraction.
    public const string Classifier = "ClassifierPrompt.v1.md";
    public const string LicenseExtraction = "LicenseExtractionPrompt.v2.md";
    public const string DeaExtraction = "DeaExtractionPrompt.v1.md";
    public const string BoardCertExtraction = "BoardCertExtractionPrompt.v1.md";
    public const string MalpracticeExtraction = "MalpracticeExtractionPrompt.v2.md";

    // Phase 4 — LLM validators.
    public const string IdentityCoherence = "IdentityCoherencePrompt.v1.md";
    // Bumped to v2 (P4 review fixes) — adds explicit prompt-injection
    // hardening for the OCR-sourced `statedSpecialty` field. v1 hashed
    // a permissive prompt that didn't isolate the trust model; v2 names
    // the field as untrusted and instructs the model to discard
    // instruction-like content inside it.
    public const string NpiTaxonomyMatch = "NpiTaxonomyMatchPrompt.v2.md";

    // Phase 5 — intake agent system prompt.
    public const string IntakeAgent = "IntakeAgentPrompt.v1.md";
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
