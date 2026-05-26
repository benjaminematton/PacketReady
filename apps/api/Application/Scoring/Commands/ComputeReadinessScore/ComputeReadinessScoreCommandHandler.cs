using MediatR;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Audit;
using PacketReady.Application.Audit.Payloads;
using PacketReady.Application.Payers;
using PacketReady.Application.Providers.Aggregation;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain.Audit;
using PacketReady.Domain.Documents;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Commands.ComputeReadinessScore;

public sealed class ComputeReadinessScoreCommandHandler
    : IRequestHandler<ComputeReadinessScoreCommand, ReadinessScoreDto>
{
    private readonly IAppDbContext _db;
    private readonly IProviderProfileAggregator _aggregator;
    private readonly IEnumerable<IValidator> _validators;
    private readonly IPayerCatalog _payers;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<ComputeReadinessScoreCommandHandler> _logger;

    public ComputeReadinessScoreCommandHandler(
        IAppDbContext db,
        IProviderProfileAggregator aggregator,
        IEnumerable<IValidator> validators,
        IPayerCatalog payers,
        IAuditWriter audit,
        TimeProvider clock,
        ILogger<ComputeReadinessScoreCommandHandler> logger)
    {
        _db = db;
        _aggregator = aggregator;
        _validators = validators;
        _payers = payers;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ReadinessScoreDto> Handle(ComputeReadinessScoreCommand request, CancellationToken ct)
    {
        // Aggregator owns provider lookup + throws ProviderNotFoundException.
        // It also returns the per-field provenance map the validators thread
        // through to populate Citation.DocumentId/Page/Bbox, plus any
        // aggregator-emitted Issues (missing-document, extraction-failed,
        // low-confidence-classification, cross-doc name mismatch).
        var aggregated = await _aggregator.AggregateAsync(request.ProviderId, ct);
        var profile = aggregated.Profile;
        var provenance = aggregated.Provenance;
        var payerId = aggregated.PayerId;

        // Fail-loud at the boundary on an unknown payer id, before any
        // validator runs — the dedicated exception type maps to a 4xx in
        // ScoreEndpoint, instead of a raw KeyNotFoundException bubbling out
        // of three different validators as an opaque 500. The catalog throws
        // PayerNotConfiguredException; we don't catch — let the API layer
        // shape it.
        var payer = _payers.Get(payerId);

        // Fan-out validators. P1 validators are synchronous and return Task.FromResult,
        // so Task.WhenAll is allocation-only here — no thread-pool work. The async
        // contract pays off in P4 when LLM-augmented validators actually overlap on the wire.
        var perValidator = await Task.WhenAll(
            _validators.Select(v => v.RunAsync(profile, provenance, payerId, ct)));

        // Merge aggregator-emitted Issues with validator output before sort+synth.
        // The dashboard branches on Severity, not source — no separate lane for
        // "DocumentStore" Issues vs validator Issues.
        //
        // Payer-aware suppression (P4 task 13): when the resolved payer has
        // BoardCertRequired=false, drop the aggregator's Missing-BoardCert
        // Critical. The aggregator emits universal-4 Missing-Document Critical
        // unconditionally per the locked ownership split (it doesn't see
        // payer config); this filter is the single legitimate place to honor
        // "board cert is optional for this payer." We filter on the typed
        // (Code, MissingDocType) discriminator stamped on Issue by the
        // aggregator's factory — substring matching the Message string would
        // silently break on any copy-edit.
        var aggregatorIssues = aggregated.Issues.AsEnumerable();
        if (!payer.BoardCertRequired)
        {
            aggregatorIssues = aggregatorIssues.Where(i =>
                !(i.Code == IssueCodes.MissingDocument
                  && i.MissingDocType == DocType.BoardCert));
        }

        var merged = aggregatorIssues
            .Concat(perValidator.SelectMany(x => x))
            .ToList();

        // P4 task 14 — confidence-threshold gate. Downgrade any Critical
        // whose citations include a low-confidence field before sort+synth,
        // so the dashboard ordering and the score both reflect the
        // post-guard Severity. ScoreSynthesizer below would otherwise count
        // a low-confidence Critical as a full Critical.
        var guarded = ConfidenceGuard.Apply(merged);

        var issues = guarded
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.Validator, StringComparer.Ordinal)
            .ToList();

        var score = ScoreSynthesizer.Compute(issues);
        var now = _clock.GetUtcNow();
        var readiness = ReadinessScore.Create(request.ProviderId, score, issues, now);
        _db.ReadinessScores.Add(readiness);

        // Count of Issues the guard downgraded. Surfaces the gate's blast
        // radius into the audit JSONB so an operator scanning ScoreComputed
        // rows can spot a packet whose tier moved because of low-confidence
        // inputs rather than the underlying credential state.
        var lowConfidenceCount = issues.Count(i => i.IsLowConfidenceInput);

        var payload = new ScoreComputedPayload(
            ProviderId: request.ProviderId,
            ReadinessScoreId: readiness.Id,
            Score: score,
            Tier: readiness.Tier.ToString(),
            CriticalCount: readiness.CriticalCount,
            MajorCount: readiness.MajorCount,
            MinorCount: readiness.MinorCount,
            ValidatorCount: perValidator.Length,
            IssueCount: issues.Count,
            LowConfidenceDowngradedCount: lowConfidenceCount);
        var evt = AuditEvent.Create(
            AuditEventType.ScoreComputed,
            payload.ToJson(),
            providerId: request.ProviderId);
        _audit.Stage(evt);

        // One SaveChanges — ReadinessScore + AuditEvent commit atomically.
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Score computed: provider={ProviderId} score={Score} tier={Tier} critical={CriticalCount} major={MajorCount} minor={MinorCount}",
            request.ProviderId, score, readiness.Tier,
            readiness.CriticalCount, readiness.MajorCount, readiness.MinorCount);

        return ReadinessScoreDto.From(readiness);
    }
}
