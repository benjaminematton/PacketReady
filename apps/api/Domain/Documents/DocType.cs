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

/// <summary>
/// Wire-format mapping for <see cref="DocType"/>. Single source of truth so the
/// classifier prompt, audit payloads, and API responses all agree on the camelCase
/// spelling. The PascalCase enum name is the DB representation; the camelCase
/// string is the external contract.
/// </summary>
public static class DocTypeWire
{
    public static string ToWireString(this DocType docType) => docType switch
    {
        DocType.License     => "license",
        DocType.Dea         => "dea",
        DocType.BoardCert   => "boardCert",
        DocType.Malpractice => "malpractice",
        DocType.Cv          => "cv",
        DocType.Other       => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(docType), docType, "Unmapped DocType."),
    };
}
