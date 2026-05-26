using System.Text.Json;
using MediatR;
using PacketReady.Application.Scoring.Commands.ComputeReadinessScore;

namespace PacketReady.Application.Intake.Agent.Tools;

/// <summary>
/// <c>compute_readiness</c> — <b>TERMINAL</b> tool. When invoked, the
/// runtime captures the score result + breaks out of the loop without
/// asking the LLM for another turn (per design.md §7.4 "Terminal action
/// pattern"). The agent's reasoning loop ends with the readiness score
/// in hand.
///
/// <para><b>Input:</b> <c>{ provider_id }</c><br/>
/// <b>Output:</b> <c>{ score, tier, issues, computed_at }</c></para>
///
/// <para>Implementation is a thin wrapper over
/// <see cref="ComputeReadinessScoreCommand"/> (P1+P3+P4 surface) —
/// avoids a parallel scoring path. Same handler that
/// <c>POST /api/providers/{id}/scores</c> hits, same audit trail.</para>
/// </summary>
public sealed class ComputeReadinessTool : IIntakeTool
{
    public string Name => "compute_readiness";

    public string Description =>
        "TERMINAL. Compute the readiness score for the provider — invoke ONLY when you believe the profile has enough credentialing data to score (e.g. license + DEA + malpractice + board cert all present and primary-source verified). Calling this ends the turn; do not call any other tool afterwards.";

    private static readonly JsonElement _schema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["provider_id"],
          "properties": {
            "provider_id": {
              "type": "string",
              "description": "UUID of the provider to score."
            }
          }
        }
        """).RootElement;

    public JsonElement InputSchema => _schema;

    public bool IsTerminal => true;

    private readonly IMediator _mediator;

    public ComputeReadinessTool(IMediator mediator) { _mediator = mediator; }

    public async Task<JsonElement> InvokeAsync(
        JsonElement args,
        Guid providerId,
        Guid turnId,
        CancellationToken ct)
    {
        // The runtime always passes the session's provider_id as the
        // ambient context; if the agent passed a different one in args
        // we honor the ambient (agent can't score someone else's
        // provider mid-turn). The argument is shape-required so the
        // schema validates, but the runtime ignores it.
        _ = args;

        var score = await _mediator.Send(new ComputeReadinessScoreCommand(providerId), ct);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            readiness_score_id = score.Id,
            score = score.Score,
            tier = score.Tier.ToString(),
            critical_count = score.CriticalCount,
            major_count = score.MajorCount,
            minor_count = score.MinorCount,
            issue_count = score.Issues.Count,
            computed_at = score.ComputedAt,
        });
        return JsonDocument.Parse(bytes).RootElement;
    }
}
