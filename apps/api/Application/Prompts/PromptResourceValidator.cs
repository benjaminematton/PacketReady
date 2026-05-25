using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PacketReady.Application.Prompts;

/// <summary>
/// Boot-time check that every key declared on <see cref="PromptKeys"/> resolves to an
/// embedded resource and produces a stable hash. Surfaces mis-globbed prompts as a
/// host-startup failure instead of waiting for the first inbound request to throw
/// <see cref="PromptNotFoundException"/>.
///
/// <para>Two reasons to also warm <see cref="PromptHasher"/> here: (1) eliminates the
/// first-extraction latency spike for hashing, since the hash is computed once at
/// boot; (2) catches ambiguity-collision errors (multiple resources matching one key)
/// at startup, alongside missing-resource errors.</para>
///
/// <para>Errors are accumulated across all keys rather than failing on the first —
/// a single boot reveals every broken key, not one boot per key.</para>
/// </summary>
public sealed class PromptResourceValidator : IHostedService
{
    private readonly IPromptLoader _prompts;
    private readonly PromptHasher _hasher;
    private readonly ILogger<PromptResourceValidator> _logger;

    public PromptResourceValidator(
        IPromptLoader prompts,
        PromptHasher hasher,
        ILogger<PromptResourceValidator> logger)
    {
        _prompts = prompts;
        _hasher = hasher;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var keys = typeof(PromptKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        var failures = new List<(string Key, Exception Error)>();
        foreach (var key in keys)
        {
            try
            {
                // HashOfAsync internally calls LoadBytesAsync, which also primes the
                // text cache on the next LoadAsync — single boot warms both paths.
                await _hasher.HashOfAsync(key, cancellationToken);
            }
            catch (Exception ex)
            {
                failures.Add((key, ex));
            }
        }

        if (failures.Count > 0)
        {
            var summary = string.Join("; ", failures.Select(f => $"{f.Key}: {f.Error.Message}"));
            throw new InvalidOperationException(
                $"Prompt resource validation failed for {failures.Count} of {keys.Count} keys. {summary}");
        }

        _logger.LogInformation("Prompt resource validation passed: {Count} prompts resolved.", keys.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
