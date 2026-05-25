using PacketReady.Domain.Documents;

namespace PacketReady.Application.Extraction.Classify;

/// <summary>
/// Single-PDF doc-type classifier — Path B's first step. Concrete impl
/// (<c>HaikuDocumentClassifier</c>) lives in Infrastructure; handlers depend
/// on this interface so they stay LLM-agnostic and Moqable.
///
/// <para>Confidence banding (≥0.85 trust / 0.50–0.85 store-with-Minor /
/// &lt;0.50 store-as-Other) is NOT done here — the classifier returns the
/// model's raw self-report; the upload handler maps to persisted doc_type.</para>
/// </summary>
public interface IDocumentClassifier
{
    Task<ClassificationResult> ClassifyAsync(
        ReadOnlyMemory<byte> pdf,
        CancellationToken ct);
}

/// <summary>
/// Classifier output. <see cref="DocType"/> is the model's predicted type
/// (camelCase wire → enum), <see cref="Confidence"/> is its self-report on
/// [0, 1], <see cref="Rationale"/> is a one-sentence explanation logged to
/// Langfuse only (not persisted). The provenance trio (<see cref="Model"/>,
/// <see cref="PromptHash"/>, token counts) lands on <c>documents.classifier_model</c>
/// + <c>documents.classifier_prompt_hash</c> for audit.
/// </summary>
public sealed record ClassificationResult(
    DocType DocType,
    double Confidence,
    string Rationale,
    string Model,
    string PromptHash,
    int InputTokens,
    int OutputTokens);
