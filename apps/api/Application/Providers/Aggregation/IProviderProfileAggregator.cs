namespace PacketReady.Application.Providers.Aggregation;

/// <summary>
/// Single source of truth for "given a provider id, what is their current
/// extracted ProviderProfile + per-field provenance + aggregator-level issues?".
/// Consumed by <c>ComputeReadinessScoreCommandHandler</c>; once slice 8 lands,
/// the score path no longer takes a hand-curated <c>ProviderProfile</c> on the
/// request body — it derives the profile from the latest succeeded extraction
/// per <c>(provider_id, doc_type)</c> via this interface.
///
/// <para>Implementation lives in Infrastructure (it reads the document_extractions
/// JSONB blobs and parses them); handlers stay Moqable through this surface.</para>
/// </summary>
public interface IProviderProfileAggregator
{
    Task<AggregatedProfile> AggregateAsync(Guid providerId, CancellationToken ct);
}
