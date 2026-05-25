using MediatR;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Audit;
using PacketReady.Application.Audit.Payloads;
using PacketReady.Application.Providers.Aggregation;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain.Audit;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Commands.ComputeReadinessScore;

public sealed class ComputeReadinessScoreCommandHandler
    : IRequestHandler<ComputeReadinessScoreCommand, ReadinessScoreDto>
{
    private readonly IAppDbContext _db;
    private readonly IProviderProfileAggregator _aggregator;
    private readonly IEnumerable<IValidator> _validators;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<ComputeReadinessScoreCommandHandler> _logger;

    public ComputeReadinessScoreCommandHandler(
        IAppDbContext db,
        IProviderProfileAggregator aggregator,
        IEnumerable<IValidator> validators,
        IAuditWriter audit,
        TimeProvider clock,
        ILogger<ComputeReadinessScoreCommandHandler> logger)
    {
        _db = db;
        _aggregator = aggregator;
        _validators = validators;
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

        // Fan-out validators. P1 validators are synchronous and return Task.FromResult,
        // so Task.WhenAll is allocation-only here — no thread-pool work. The async
        // contract pays off in P4 when LLM-augmented validators actually overlap on the wire.
        var perValidator = await Task.WhenAll(
            _validators.Select(v => v.RunAsync(profile, provenance, ct)));

        // Merge aggregator-emitted Issues with validator output before sort+synth.
        // The dashboard branches on Severity, not source — no separate lane for
        // "DocumentStore" Issues vs validator Issues.
        var issues = aggregated.Issues
            .Concat(perValidator.SelectMany(x => x))
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.Validator, StringComparer.Ordinal)
            .ToList();

        var score = ScoreSynthesizer.Compute(issues);
        var now = _clock.GetUtcNow();
        var readiness = ReadinessScore.Create(request.ProviderId, score, issues, now);
        _db.ReadinessScores.Add(readiness);

        var payload = new ScoreComputedPayload(
            ProviderId: request.ProviderId,
            ReadinessScoreId: readiness.Id,
            Score: score,
            Tier: readiness.Tier.ToString(),
            CriticalCount: readiness.CriticalCount,
            MajorCount: readiness.MajorCount,
            MinorCount: readiness.MinorCount,
            ValidatorCount: perValidator.Length,
            IssueCount: issues.Count);
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
