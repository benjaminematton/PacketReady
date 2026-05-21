using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Audit;
using PacketReady.Application.Audit.Payloads;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain.Audit;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Commands.ComputeReadinessScore;

public sealed class ComputeReadinessScoreCommandHandler
    : IRequestHandler<ComputeReadinessScoreCommand, ReadinessScoreDto>
{
    private readonly IAppDbContext _db;
    private readonly IEnumerable<IValidator> _validators;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<ComputeReadinessScoreCommandHandler> _logger;

    public ComputeReadinessScoreCommandHandler(
        IAppDbContext db,
        IEnumerable<IValidator> validators,
        IAuditWriter audit,
        TimeProvider clock,
        ILogger<ComputeReadinessScoreCommandHandler> logger)
    {
        _db = db;
        _validators = validators;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ReadinessScoreDto> Handle(ComputeReadinessScoreCommand request, CancellationToken ct)
    {
        // AsNoTracking — we're only reading the provider; the writes in this handler
        // are the new ReadinessScore + AuditEvent, neither of which mutates Provider.
        var provider = await _db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.ProviderId, ct)
            ?? throw new ProviderNotFoundException(request.ProviderId);

        var profile = provider.GetProfile();

        // Fan-out validators. P1 validators are synchronous and return Task.FromResult,
        // so Task.WhenAll is allocation-only here — no thread-pool work. The async
        // contract pays off in P4 when LLM-augmented validators actually overlap on the wire.
        var perValidator = await Task.WhenAll(_validators.Select(v => v.RunAsync(profile, ct)));

        // Sort Severity DESC (Critical first) then Validator name ASC for stable
        // ordering — the side-panel renders in this order, and tests compare lists.
        var issues = perValidator
            .SelectMany(x => x)
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.Validator, StringComparer.Ordinal)
            .ToList();

        var score = ScoreSynthesizer.Compute(issues);
        var now = _clock.GetUtcNow();
        var readiness = ReadinessScore.Create(provider.Id, score, issues, now);
        _db.ReadinessScores.Add(readiness);

        var payload = new ScoreComputedPayload(
            ProviderId: provider.Id,
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
            providerId: provider.Id);
        _audit.Stage(evt);

        // One SaveChanges — ReadinessScore + AuditEvent commit atomically.
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Score computed: provider={ProviderId} score={Score} tier={Tier} critical={CriticalCount} major={MajorCount} minor={MinorCount}",
            provider.Id, score, readiness.Tier,
            readiness.CriticalCount, readiness.MajorCount, readiness.MinorCount);

        return new ReadinessScoreDto(
            Id: readiness.Id,
            ProviderId: provider.Id,
            Score: score,
            Tier: readiness.Tier,
            CriticalCount: readiness.CriticalCount,
            MajorCount: readiness.MajorCount,
            MinorCount: readiness.MinorCount,
            Issues: issues,
            ComputedAt: now);
    }
}
