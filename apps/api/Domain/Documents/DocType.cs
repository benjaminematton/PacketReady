namespace PacketReady.Domain.Documents;

/// <summary>
/// Classifier output and per-extractor schema discriminator. Stored as TEXT in the
/// <c>documents.doc_type</c> column using the enum's PascalCase name; the DB-side
/// check constraint pins the value set. JSON wire uses camelCase
/// (<c>"docType": "license"</c>) — the classifier maps camelCase → enum at the
/// boundary.
///
/// <para><see cref="Other"/> is the &lt;0.50 confidence fallback per spec §"Classifier
/// confidence bands"; aggregator skips Other rows when building <c>ProviderProfile</c>.</para>
/// </summary>
public enum DocType
{
    License,
    Dea,
    BoardCert,
    Malpractice,
    Cv,
    Other,
}
