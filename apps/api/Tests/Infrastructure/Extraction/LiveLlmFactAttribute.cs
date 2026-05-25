using Xunit;

namespace PacketReady.Tests.Infrastructure.Extraction;

/// <summary>
/// Marks a fact that calls the live Anthropic API. xUnit evaluates
/// <see cref="FactAttribute.Skip"/> at discovery time, so this attribute
/// inspects env vars when the test class loads:
///
/// <list type="bullet">
///   <item><c>PACKETREADY_LIVE_LLM=1</c> — explicit opt-in flag</item>
///   <item><c>ANTHROPIC_API_KEY=…</c> — credentials</item>
/// </list>
///
/// Missing either skips with a reason xUnit surfaces in the runner output.
/// CI runs without both and so never bills Anthropic.
/// </summary>
public sealed class LiveLlmFactAttribute : FactAttribute
{
    public LiveLlmFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PACKETREADY_LIVE_LLM") != "1")
        {
            Skip = "PACKETREADY_LIVE_LLM is not '1' — live-LLM test skipped (opt-in only).";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
        {
            Skip = "ANTHROPIC_API_KEY is not set — cannot exercise the live Anthropic API.";
        }
    }
}
