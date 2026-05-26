namespace PacketReady.Application.Nucc;

/// <summary>
/// Static <c>taxonomy code → canonical specialty</c> table loaded once at
/// startup from the committed NUCC snapshot at <c>data/nucc-taxonomy-25.1.csv</c>.
/// Consumed by <c>NpiTaxonomyMatchValidator</c>'s step-1 lookup. The
/// validator's step-2 (a thin LLM call) only sees the canonical specialty
/// label, not the table — sending all ~900 rows to Sonnet on every call
/// would burn ~30k input tokens for no benefit.
///
/// <para><see cref="TryGet"/> is the only consumer surface; the
/// implementation is a Dictionary wrap, but the abstraction lets the
/// validator stay free of file-I/O concerns and the loader stay free
/// of validator concerns.</para>
///
/// <para>NUCC publishes biannually with version label <c>YY.0</c>
/// (January) / <c>YY.1</c> (July). Bumping is a snapshot + filename
/// change; the lookup shape is stable.</para>
/// </summary>
public interface INuccTaxonomyLookup
{
    /// <summary>
    /// Map a 10-character NUCC taxonomy code (e.g. <c>"207R00000X"</c>) to
    /// its canonical specialty display name (e.g.
    /// <c>"Internal Medicine Physician"</c>). Returns <c>false</c> on miss
    /// — code is unrecognized, malformed, or from a NUCC revision newer
    /// than the snapshot. The validator treats a miss as "no signal" and
    /// emits no Issue rather than fabricating one.
    /// </summary>
    bool TryGet(string taxonomyCode, out string canonicalSpecialty);

    /// <summary>Row count in the loaded snapshot — useful for startup
    /// logging and a smoke test that the file actually parsed.</summary>
    int Count { get; }
}
