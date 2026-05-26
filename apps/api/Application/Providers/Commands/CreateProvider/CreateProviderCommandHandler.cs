using MediatR;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Payers;
using PacketReady.Domain.Providers;

namespace PacketReady.Application.Providers.Commands.CreateProvider;

/// <summary>
/// Handles <see cref="CreateProviderCommand"/>. Contract:
/// <list type="bullet">
///   <item><b>PayerId</b>: <c>null</c> defaults to
///         <see cref="Provider.DefaultPayerId"/>. Anything else is passed
///         through to <see cref="IPayerCatalog.Get"/>; a miss throws
///         <see cref="PayerNotConfiguredException"/> which the endpoint
///         maps to 422. Whitespace is rejected by
///         <see cref="Provider.Create"/> as <see cref="ArgumentException"/>
///         — the endpoint pre-validates this case so it never reaches the
///         handler from an HTTP caller.</item>
///   <item><b>Identity</b>: <c>null</c> uses
///         <see cref="ProviderIdentityValidator.Placeholder"/>. A supplied
///         identity is delegated to <see cref="ProviderProfile.Validate"/>
///         (via <see cref="ProviderProfile.Create"/>) for domain-shape
///         enforcement.</item>
///   <item><b>IntakeBudgetTurns</b>: <c>null</c> defaults to
///         <see cref="Provider.DefaultIntakeBudgetTurns"/>. Non-positive
///         values are rejected at the endpoint (400) and again at
///         <see cref="Provider.Create"/> as
///         <see cref="ArgumentOutOfRangeException"/> for the
///         non-HTTP-caller path.</item>
/// </list>
///
/// <para><b>Luhn lives at the wire boundary, not here.</b> The handler does
/// not re-run <see cref="ProviderIdentityValidator.Validate"/>; it relies on
/// the endpoint to have done so. A mediator-only caller with a non-Luhn but
/// otherwise valid 10-digit NPI will succeed. That matches the rest of the
/// codebase: domain-shape invariants live in the domain; wire-format
/// invariants (Luhn, JSON shape) live at the boundary.</para>
/// </summary>
public sealed class CreateProviderCommandHandler
    : IRequestHandler<CreateProviderCommand, Guid>
{
    private readonly IAppDbContext _db;
    private readonly IPayerCatalog _payers;
    private readonly TimeProvider _clock;

    public CreateProviderCommandHandler(
        IAppDbContext db,
        IPayerCatalog payers,
        TimeProvider clock)
    {
        _db = db;
        _payers = payers;
        _clock = clock;
    }

    public async Task<Guid> Handle(CreateProviderCommand request, CancellationToken ct)
    {
        // Only null falls back to the default. Whitespace would otherwise be
        // silently coerced to `payer-a-national-hmo`, which contradicts the
        // domain (Provider.Create throws on whitespace). The endpoint
        // pre-validates wire shape, so a whitespace PayerId here means a
        // non-HTTP caller has a bug worth surfacing — match the domain's
        // ArgumentException directly rather than routing through the catalog
        // and surfacing a misleading PayerNotConfiguredException for what is
        // really an input-shape error.
        if (request.PayerId is not null && string.IsNullOrWhiteSpace(request.PayerId))
            throw new ArgumentException(
                "PayerId must be null (to use the default) or a non-whitespace value.",
                nameof(request));

        var resolvedPayerId = request.PayerId ?? Provider.DefaultPayerId;

        // Throws PayerNotConfiguredException → 422 at the endpoint. Same
        // contract as ComputeReadinessScoreCommandHandler's pre-flight
        // resolution — payer resolution is the API boundary, not the
        // validator surface.
        _ = _payers.Get(resolvedPayerId);

        var identity = request.Identity ?? ProviderIdentityValidator.Placeholder;
        var now = _clock.GetUtcNow();

        // Build a minimal ProviderProfile. The aggregator overlays
        // extractions on top of this at score time; only the identity
        // fields are load-bearing for non-credential validators today.
        var profile = ProviderProfile.Create(
            fullName: identity.FullName,
            dateOfBirth: identity.DateOfBirth,
            npi: identity.Npi,
            credentialingState: identity.CredentialingState,
            nowUtc: now);

        var provider = Provider.Create(
            profile,
            now,
            payerId: resolvedPayerId,
            intakeBudgetTurns: request.IntakeBudgetTurns);
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync(ct);

        return provider.Id;
    }
}
