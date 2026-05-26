using PacketReady.Application.Providers.Aggregation;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Validators;

/// <summary>
/// One validation dimension. May emit zero, one, or many <see cref="Issue"/>s per
/// run — no short-circuit. The handler runs all validators in parallel and unions
/// their output before computing the score.
///
/// <para><see cref="Name"/> is the stable identifier carried on every emitted Issue
/// (and on the Issue's Citations). Tests and the dashboard cross-reference by this
/// string; do not rename without a coordinated change.</para>
///
/// <para><paramref name="provenance"/> (added P3 slice 8) is the per-field
/// citation map produced by <c>IProviderProfileAggregator</c>; validators look up
/// <c>"&lt;docType&gt;.&lt;fieldName&gt;"</c> and attach the result to every Citation
/// they emit. Sanctions has no source document → leaves Citation doc-ref fields
/// null. Validators stay pure-code (no DB access); the provenance flows in as a
/// method parameter, never via service-locator.</para>
///
/// <para><paramref name="payerId"/> (added P4) is the <c>Provider.PayerId</c> the
/// handler resolved for this run. Payer-aware validators (malpractice currency,
/// required documents, board cert) look it up in the singleton
/// <c>IReadOnlyDictionary&lt;string, PayerRequirement&gt;</c> to read per-payer
/// minimums / accepted boards / required-doc lists. Validators that don't care
/// about payer config (license, dea, sanctions, identity coherence) ignore it.</para>
/// </summary>
public interface IValidator
{
    string Name { get; }

    Task<IReadOnlyList<Issue>> RunAsync(
        ProviderProfile profile,
        IReadOnlyDictionary<string, FieldProvenance> provenance,
        string payerId,
        CancellationToken ct);
}
