using Microsoft.Extensions.DependencyInjection;
using PacketReady.Application.Prompts;
using PacketReady.Application.Scoring.Validators;

namespace PacketReady.Application;

/// <summary>
/// Application-layer DI registrations. Currently only the validator suite — MediatR
/// scans the Application assembly from <c>Program.cs</c>, so handlers don't need
/// explicit registration here.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Explicit per-validator registration (not assembly scan) for two reasons:
        //   1. The handler injects IEnumerable<IValidator> — DI needs each impl
        //      registered against the IValidator service type, not the concrete.
        //   2. Adding a new validator is a one-liner; the explicit list is easier
        //      to audit than a reflection-based scan.
        services.AddScoped<IValidator, LicenseStatusValidator>();
        services.AddScoped<IValidator, DeaStatusValidator>();
        services.AddScoped<IValidator, BoardCertificationValidator>();
        services.AddScoped<IValidator, SanctionsCheckValidator>();
        // P4 task 8 — first LLM validator. Takes IChatClient + IPromptLoader,
        // pinned to claude-sonnet-4-6 inside the class. Prompt tuning happens
        // on the 10-packet subset (see evals/runners/runners/tuning_subsets.py
        // and P4 task 9); the regression gate's FP/recall numbers in
        // baseline.json are tied to (this prompt × this model id).
        services.AddScoped<IValidator, IdentityCoherenceValidator>();

        // TimeProvider.System is the default for production. Validator unit tests
        // construct validators directly with FakeTimeProvider, bypassing this
        // registration entirely; the singleton is here for non-test consumers.
        services.AddSingleton(TimeProvider.System);

        // PromptHasher caches per-prompt SHA-256s; singleton matches that.
        // It depends only on IPromptLoader (already singleton in Infrastructure).
        services.AddSingleton<PromptHasher>();

        // Re-publish the built-in ServiceProvider as IKeyedServiceProvider so
        // handlers can inject the tighter type for keyed-extractor lookups.
        // The default ServiceProvider implements the interface; .NET's
        // validate-on-build step refuses to construct it without a descriptor,
        // so we add one that just returns the provider itself.
        services.AddScoped<IKeyedServiceProvider>(sp => (IKeyedServiceProvider)sp);

        return services;
    }
}
