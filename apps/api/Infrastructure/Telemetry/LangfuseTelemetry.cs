namespace PacketReady.Infrastructure.Telemetry;

/// <summary>
/// Centralizes Langfuse-specific tag and score names. Ported from VaBene without
/// semantic change — see Appendix C.7 of design.md for provenance.
/// </summary>
public static class LangfuseTelemetry
{
    public const string ActivitySourceName = "PacketReady";

    public static class Tags
    {
        /// <summary>Groups spans into one Langfuse session view.</summary>
        public const string SessionId = "langfuse.session.id";

        /// <summary>Attributes a span to a user (operator who started the flow).</summary>
        public const string UserId = "langfuse.user.id";

        /// <summary>Renders as the trace's "input" in Langfuse.</summary>
        public const string ObservationInput = "langfuse.observation.input";

        /// <summary>Renders as the trace's "output" in Langfuse.</summary>
        public const string ObservationOutput = "langfuse.observation.output";
    }

    public static class Scores
    {
        /// <summary>Terminal good/bad/neutral signal. 1 good, 0 neutral, -1 bad.</summary>
        public const string Outcome = "outcome";
    }

    /// <summary>
    /// Suffixes a Langfuse OTLP endpoint can end with. The .NET OTel exporter using
    /// <c>HttpProtobuf</c> requires the full signal path (<c>/v1/traces</c>); other
    /// SDKs auto-append. Accept either form so config copy/paste from Langfuse docs
    /// works regardless of which form the operator pasted.
    /// </summary>
    public static readonly IReadOnlyList<string> OtlpEndpointSuffixes =
    [
        "/api/public/otel/v1/traces",
        "/api/public/otel",
    ];

    /// <summary>
    /// Derives the Langfuse REST API base from an OTLP endpoint. Not used in Phase 0 —
    /// reserved for the Phase 4+ score-posting path (POST /api/public/scores), which
    /// hits Langfuse REST rather than OTLP. Kept here so callers don't reparse the env
    /// var when that lands.
    /// </summary>
    public static bool TryGetApiBase(string? otlpEndpoint, out string apiBase)
    {
        apiBase = string.Empty;
        if (string.IsNullOrWhiteSpace(otlpEndpoint)) return false;

        var trimmed = otlpEndpoint.TrimEnd('/');
        foreach (var suffix in OtlpEndpointSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                apiBase = trimmed[..^suffix.Length];
                return true;
            }
        }
        return false;
    }
}
