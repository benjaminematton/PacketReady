using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PacketReady.Application.Prompts;

/// <summary>
/// Boot-time check that every key declared on <see cref="PromptKeys"/> resolves to an
/// embedded resource. Surfaces mis-globbed prompts as a host-startup failure instead
/// of waiting for the first inbound request to throw <see cref="PromptNotFoundException"/>.
///
/// <para>Phase 0: <see cref="PromptKeys"/> is empty; this hosted service runs and logs
/// "0 prompts resolved" at startup. Phase 3 onward will populate it.</para>
/// </summary>
public sealed class PromptResourceValidator : IHostedService
{
    private readonly IPromptLoader _prompts;
    private readonly ILogger<PromptResourceValidator> _logger;

    public PromptResourceValidator(IPromptLoader prompts, ILogger<PromptResourceValidator> logger)
    {
        _prompts = prompts;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var keys = typeof(PromptKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        await Task.WhenAll(keys.Select(k => _prompts.LoadAsync(k, cancellationToken)));

        _logger.LogInformation("Prompt resource validation passed: {Count} prompts resolved.", keys.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
