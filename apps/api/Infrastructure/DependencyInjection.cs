using Anthropic.SDK;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Audit;
using PacketReady.Application.Prompts;
using PacketReady.Infrastructure.Audit;
using PacketReady.Infrastructure.Persistence;

namespace PacketReady.Infrastructure;

/// <summary>
/// Infrastructure DI wiring, split so non-API binaries (e.g. <c>tools/Seed</c>) can
/// take the DB + audit slice without dragging in the Anthropic SDK and a required
/// API key. <see cref="AddPersistence"/> is the shared minimum; <see cref="AddInfrastructure"/>
/// adds the LLM client and prompt loader the API itself needs.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// DB context, scoped abstractions, and the audit writer. Safe to call from any
    /// binary that talks to the database; doesn't require <c>ANTHROPIC_API_KEY</c>.
    /// </summary>
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration config)
    {
        var connStr = config["DB_CONNECTION_STRING"]
            ?? throw new InvalidOperationException("DB_CONNECTION_STRING is not configured.");

        // Factory-first: a single options builder, then the scoped DbContext pulls
        // from the factory. Avoids double-registering Npgsql options and the
        // ambiguity of "is the scoped instance the same context the factory makes?"
        services.AddDbContextFactory<PacketReadyDbContext>(o => o.UseNpgsql(connStr));
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<PacketReadyDbContext>>().CreateDbContext());

        // PacketReadyDbContext implements both IAppDbContext and IUnitOfWork; resolve
        // the same scoped instance for both so reads, writes, and SaveChanges share state.
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<PacketReadyDbContext>());
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<PacketReadyDbContext>());

        services.AddScoped<IAuditWriter, AuditWriter>();

        return services;
    }

    /// <summary>
    /// Full Infrastructure surface: persistence plus the Anthropic client and prompt
    /// loader the API host requires. Throws if <c>ANTHROPIC_API_KEY</c> is missing.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.AddPersistence(config);
        services.AddSingleton<IPromptLoader, PromptLoader>();

        var apiKey = config["ANTHROPIC_API_KEY"]
            ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not configured.");
        services.AddSingleton(_ => new AnthropicClient(apiKey));
        services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<AnthropicClient>().Messages);

        return services;
    }
}
