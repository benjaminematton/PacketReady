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
/// Single entry point for Infrastructure DI wiring. Program.cs calls
/// <see cref="AddInfrastructure"/> with the bound config; this file decides what's
/// registered. Keeps Program.cs from accumulating provider-specific concerns.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
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
        services.AddSingleton<IPromptLoader, PromptLoader>();

        var apiKey = config["ANTHROPIC_API_KEY"]
            ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not configured.");
        services.AddSingleton(_ => new AnthropicClient(apiKey));
        services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<AnthropicClient>().Messages);

        return services;
    }
}
