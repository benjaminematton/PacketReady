using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Providers.Aggregation;

/// <summary>
/// Output of <see cref="IProviderProfileAggregator"/>. Carries the
/// reconstructed <see cref="ProviderProfile"/> alongside the per-field
/// provenance map the validators consume, plus the aggregator's own emitted
/// <see cref="Issue"/>s for cases that don't fit any validator's lane.
///
/// <para>The aggregator emits <see cref="Issue"/>s for:
/// <list type="bullet">
///   <item><b>Missing-Document Critical</b> — provider has no document of an
///   expected type (License, DEA, BoardCert, Malpractice). No citation;
///   there's nothing to point at.</item>
///   <item><b>Extraction-Failed Critical</b> — latest extraction for a doc
///   type has <c>status='Failed'</c>; the persisted <c>error</c> is the
///   Issue message detail. Citation points at the document.</item>
///   <item><b>Low-confidence-classification Minor</b> — the document's
///   classifier confidence landed in the 0.50–0.85 mid-band per spec
///   §"Classifier runtime fallback".</item>
///   <item><b>Cross-doc name mismatch Minor</b> — fullName disagrees across
///   doc types by Levenshtein ≥ 3 (license precedence wins for the persisted
///   <see cref="ProviderProfile.FullName"/>).</item>
/// </list>
/// </para>
///
/// <para>The score-compute handler merges these Issues with validator output
/// before <c>ScoreSynthesizer.Compute</c> — no separate type for "aggregation"
/// vs "validator" issues; the dashboard branches on Severity, not source.</para>
///
/// <para><see cref="PayerId"/> (added P4) is the value of <c>Provider.PayerId</c>
/// the aggregator read from the database. The handler threads it into every
/// validator's <c>RunAsync</c>; payer-aware validators resolve it against the
/// singleton <c>IReadOnlyDictionary&lt;string, PayerRequirement&gt;</c> to fetch
/// minimums / accepted boards / required-doc lists.</para>
/// </summary>
public sealed record AggregatedProfile(
    ProviderProfile Profile,
    IReadOnlyDictionary<string, FieldProvenance> Provenance,
    IReadOnlyList<Issue> Issues,
    string PayerId);
