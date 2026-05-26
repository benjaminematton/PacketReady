using PacketReady.Application.Payers;
using PacketReady.Application.Providers.Aggregation;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Validators;

/// <summary>
/// Checks board certification. Phase 1 shipped presence/status/expiry; Phase 4
/// extends with two optional payer-config branches.
/// <list type="bullet">
///   <item>Critical — status is not Active.</item>
///   <item>Critical — expiry strictly before today.</item>
///   <item>Minor — still valid but expires within 30 days.</item>
///   <item>Major (P4) — extracted board not on the payer's
///         <see cref="PayerRequirement.AcceptedBoards"/> list (when the list is
///         populated; empty list means "any board is fine").</item>
/// </list>
///
/// <para>Missing-board-cert is owned by the aggregator
/// (see <c>ProviderProfileAggregator.MissingDocumentIssue</c>); this validator
/// short-circuits when <see cref="ProviderProfile.BoardCert"/> is null. The
/// companion suppression — dropping the aggregator's Missing-BoardCert
/// Critical when <c>payer.BoardCertRequired == false</c> — lives in
/// <c>ComputeReadinessScoreCommandHandler</c>, the only place that can see
/// both the aggregator-emitted and validator-emitted Issue streams. The
/// handler filters on the typed (Code, MissingDocType) discriminator stamped
/// on Issue, not on the message string.</para>
/// </summary>
public sealed class BoardCertificationValidator : IValidator
{
    public string Name => "board_certification";

    private readonly TimeProvider _clock;
    private readonly IPayerCatalog _payers;

    public BoardCertificationValidator(
        TimeProvider clock,
        IPayerCatalog payers)
    {
        _clock = clock;
        _payers = payers;
    }

    public Task<IReadOnlyList<Issue>> RunAsync(
        ProviderProfile profile,
        IReadOnlyDictionary<string, FieldProvenance> provenance,
        string payerId,
        CancellationToken ct)
    {
        if (profile.BoardCert is null)
            return Task.FromResult<IReadOnlyList<Issue>>(Array.Empty<Issue>());

        var payer = _payers.Get(payerId);

        // Defensive guard. The loader rejects `BoardCertRequired=false` with a
        // non-empty AcceptedBoards list, so in practice this branch only fires
        // when the loader is bypassed or someone constructs a PayerRequirement
        // in code. Without this, a payer that "doesn't require board cert"
        // would still emit a Major if any AcceptedBoards row ever slipped in.
        if (!payer.BoardCertRequired)
            return Task.FromResult<IReadOnlyList<Issue>>(Array.Empty<Issue>());

        var issues = new List<Issue>();
        var today = _clock.Today();
        var bc = profile.BoardCert;

        // Parallel citations: status Issue → status field on the PDF; expiry
        // Issue → expiry field on the PDF; board-not-accepted → board field.
        IReadOnlyList<Citation> statusCite = [provenance.Cite(
            Name,
            $"{bc.Board} {bc.Specialty} status={bc.Status}",
            "boardCert.status")];
        IReadOnlyList<Citation> expiryCite = [provenance.Cite(
            Name,
            $"{bc.Board} {bc.Specialty} expires={bc.ExpiryDate:yyyy-MM-dd}",
            "boardCert.expiryDate")];

        if (bc.Status != BoardCertStatus.Active)
            issues.Add(new Issue(Name, Severity.Critical,
                $"Board cert status is {bc.Status}; must be Active.",
                "Confirm current certification with the issuing board.", statusCite));

        if (bc.ExpiryDate < today)
            issues.Add(new Issue(Name, Severity.Critical,
                $"Board cert expired on {bc.ExpiryDate:yyyy-MM-dd}.",
                "Renew or recertify with the issuing board before submission.", expiryCite));

        // Renewal-window Minor only fires when the cert is still Active AND
        // unexpired — mirrors the malpractice/license discipline so a Lapsed
        // cert with a future printed ExpiryDate doesn't stack a Minor on top
        // of an already-emitted status Critical.
        if (bc.Status == BoardCertStatus.Active
            && bc.ExpiryDate >= today
            && (bc.ExpiryDate.DayNumber - today.DayNumber) < 30)
            issues.Add(new Issue(Name, Severity.Minor,
                $"Board cert expires in {bc.ExpiryDate.DayNumber - today.DayNumber} days.",
                "Recertification recommended before payer submission.", expiryCite));

        // P4 — accepted-board check. Empty list means "any board is fine"
        // (matches the loader's contract: BoardCertRequired=false enforces
        // empty AcceptedBoards). String compare is Ordinal because boards
        // are short acronyms; case sensitivity matches the YAML schema.
        if (payer.AcceptedBoards.Length > 0
            && !payer.AcceptedBoards.Contains(bc.Board, StringComparer.Ordinal))
        {
            IReadOnlyList<Citation> boardCite = [provenance.Cite(
                Name,
                $"{bc.Board} {bc.Specialty}",
                "boardCert.board")];
            issues.Add(new Issue(Name, Severity.Major,
                $"Board '{bc.Board}' is not on the accepted list for {payer.Name}; payer accepts [{string.Join(", ", payer.AcceptedBoards)}].",
                $"Confirm certification with a {payer.Name}-accepted board before submission.",
                boardCite));
        }

        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }
}
