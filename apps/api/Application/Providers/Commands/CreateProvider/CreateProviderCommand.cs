using MediatR;

namespace PacketReady.Application.Providers.Commands.CreateProvider;

/// <summary>
/// Creates a Provider row in a minimal state. Phase 4 (the eval orchestrator)
/// is the first non-CLI caller; Phase 5 (admin intake) is the second.
///
/// <para>The endpoint takes only <see cref="PayerId"/> and an optional
/// <see cref="Identity"/> block. <see cref="PayerId"/> defaults to
/// <c>Provider.DefaultPayerId</c> when null. <see cref="Identity"/> defaults
/// to a placeholder profile (synthetic Luhn-valid NPI, state <c>XX</c>, etc.)
/// that satisfies <c>ProviderProfile.Validate</c> without claiming real-world
/// values — the P5 admin-intake flow uses this path before any provider data
/// arrives, then fills via the intake portal.</para>
///
/// <para><b>Why identity isn't pulled from extractions:</b> the aggregator
/// currently overlays extracted credentials on top of stored basics. Per
/// <c>ProviderProfileAggregator</c>'s class docstring, P3 doesn't extract
/// NPI/DOB/state from any doc type; those still come from the stored
/// profile. P4's CV extractor (not shipped) will close the seam — at that
/// point this endpoint's <see cref="Identity"/> input becomes optional
/// override rather than required-for-honest-scoring.</para>
/// </summary>
public sealed record CreateProviderCommand(
    string? PayerId,
    ProviderIdentityDto? Identity,
    int? IntakeBudgetTurns = null) : IRequest<Guid>;

/// <summary>
/// Identity fields needed by validators that don't (yet) have an extraction
/// source. Mirrors the shape of the not-yet-shipped CV extractor's output, so
/// the migration to extraction-only is a wire-format swap, not a refactor.
///
/// <para>All four fields are required when the block is present. Validation
/// runs at the endpoint boundary — see
/// <see cref="ProviderIdentityValidator"/> — and rejects with the full
/// list of violations, not just the first.</para>
///
/// <para>This record is shared by the wire (<c>CreateProviderRequest.Identity</c>)
/// and the mediator command. Identity has the same shape on both sides today;
/// the outer wrapper records are split only so an outer-shape addition
/// (e.g. P5 <c>adminInitiatedBy</c>) doesn't drag the mediator contract.
/// If identity itself ever diverges between wire and command, introduce a
/// dedicated <c>ProviderIdentityWireDto</c> and map at the endpoint.</para>
/// </summary>
public sealed record ProviderIdentityDto(
    string FullName,
    string Npi,
    DateOnly DateOfBirth,
    string CredentialingState);
