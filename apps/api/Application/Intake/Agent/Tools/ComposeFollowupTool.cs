using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Intake.Outbox;

namespace PacketReady.Application.Intake.Agent.Tools;

/// <summary>
/// <c>compose_followup</c> — turn a gap list into a single email subject +
/// body. Delegates the actual formatting to
/// <see cref="ComposeFollowupHandler"/> (pure-code, deterministic).
///
/// <para><b>Input:</b> <c>{ provider_id, gaps: [{ kind, message, remediation_hint? }] }</c><br/>
/// <b>Output:</b> <c>{ subject, body }</c></para>
///
/// <para>The agent does NOT send the email — the runtime composes an
/// <c>OutboundMessage</c> from this output on its way out of the turn,
/// and the dispatcher (C5) handles dispatch. Per design.md §7.4 "Why no
/// send_email tool" — the LLM proposes content, deterministic code
/// commits and sends.</para>
/// </summary>
public sealed class ComposeFollowupTool : IIntakeTool
{
    public string Name => "compose_followup";

    public string Description =>
        "Compose ONE consolidated followup email for the provider, listing every gap in one message. Do NOT call this per-gap — collect every gap into one call. The runtime will queue the message for dispatch; you do not send email directly.";

    private static readonly JsonElement _schema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["provider_id", "gaps"],
          "properties": {
            "provider_id": {
              "type": "string",
              "description": "UUID of the provider this followup is for."
            },
            "gaps": {
              "type": "array",
              "description": "Every outstanding item, one entry per gap. Compose ONE message that lists them all.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["kind", "message"],
                "properties": {
                  "kind": {
                    "type": "string",
                    "description": "Short tag — e.g. 'missing_dea', 'unclear_license_state'."
                  },
                  "message": {
                    "type": "string",
                    "description": "Provider-facing prose for this gap (1–2 sentences)."
                  },
                  "remediation_hint": {
                    "type": "string",
                    "description": "Optional concrete next step. Renders parenthetically."
                  }
                }
              }
            }
          }
        }
        """).RootElement;

    public JsonElement InputSchema => _schema;

    private readonly IAppDbContext _db;
    private readonly ComposeFollowupHandler _handler;

    public ComposeFollowupTool(IAppDbContext db, ComposeFollowupHandler handler)
    {
        _db = db;
        _handler = handler;
    }

    public async Task<JsonElement> InvokeAsync(
        JsonElement args,
        Guid providerId,
        Guid turnId,
        CancellationToken ct)
    {
        if (!ToolArgs.TryReadArray(args, "gaps", out var gapsEl))
            return ToolResults.Error("gaps is required and must be a non-empty array.");

        var gaps = new List<ComposeFollowupHandler.Gap>();
        foreach (var g in gapsEl.EnumerateArray())
        {
            if (!ToolArgs.TryReadString(g, "kind", out var kind))
                return ToolResults.Error("each gap must include a 'kind' string.");
            if (!ToolArgs.TryReadString(g, "message", out var message))
                return ToolResults.Error("each gap must include a 'message' string.");

            string? hint = null;
            if (g.TryGetProperty("remediation_hint", out var hintEl)
                && hintEl.ValueKind == JsonValueKind.String)
            {
                hint = hintEl.GetString();
            }
            gaps.Add(new ComposeFollowupHandler.Gap(kind, message, hint));
        }

        if (gaps.Count == 0)
            return ToolResults.Error("gaps must not be empty. Invoke compute_readiness if no gaps remain.");

        // Best-effort name pluck — same shape as the portal endpoint. A
        // missing profile lands as "there" via the handler's fallback.
        var providerFullName = await _db.Providers
            .AsNoTracking()
            .Where(p => p.Id == providerId)
            .Select(p => p.ProfileJson)
            .SingleOrDefaultAsync(ct) is { } profileJson
            ? TryExtractFullName(profileJson)
            : null;

        var followup = _handler.Compose(providerFullName ?? "there", gaps);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            subject = followup.Subject,
            body = followup.Body,
        });
        return JsonDocument.Parse(bytes).RootElement;
    }

    private static string? TryExtractFullName(string profileJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(profileJson);
            return doc.RootElement.TryGetProperty("fullName", out var el)
                && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
