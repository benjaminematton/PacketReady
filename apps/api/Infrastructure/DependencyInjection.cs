using Anthropic.SDK;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Audit;
using PacketReady.Application.Documents;
using PacketReady.Application.Extraction.Classify;
using PacketReady.Application.Extraction.Extract;
using PacketReady.Application.Extraction.Persist;
using PacketReady.Application.Prompts;
using PacketReady.Application.Providers.Aggregation;
using PacketReady.Domain.Documents;
using PacketReady.Infrastructure.Audit;
using PacketReady.Infrastructure.Blob;
using PacketReady.Infrastructure.Extraction;
using PacketReady.Infrastructure.Extraction.Classifier;
using PacketReady.Infrastructure.Extraction.SonnetExtractors;
using PacketReady.Infrastructure.Persistence;
using PacketReady.Infrastructure.Providers;

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

        // PdfPageCounter is stateless after construction; singleton matches the
        // upstream UglyToad.PdfPig usage pattern (per-call PdfDocument.Open).
        services.AddSingleton<PdfPageCounter>();

        // Extractors: keyed by DocType, dispatched from ExtractInMemoryCommandHandler
        // (Path A) and the upload handler (Path B). Singleton because every dep
        // along the chain is singleton — no per-request state in the extractor.
        // CV (DocType.Cv) is P4; the endpoint's GetKeyedService probe returns
        // 400 for unregistered types, so leaving it absent here is the gate.
        services.AddKeyedSingleton<IDocTypeExtractor, LicenseExtractor>(DocType.License);
        services.AddKeyedSingleton<IDocTypeExtractor, DeaExtractor>(DocType.Dea);
        services.AddKeyedSingleton<IDocTypeExtractor, BoardCertExtractor>(DocType.BoardCert);
        services.AddKeyedSingleton<IDocTypeExtractor, MalpracticeExtractor>(DocType.Malpractice);

        // Single Haiku-backed classifier — Path B's first step. Stateless after
        // construction; singleton.
        services.AddSingleton<IDocumentClassifier, HaikuDocumentClassifier>();

        // ExtractionPersister depends on the scoped DbContext, so itself scoped.
        services.AddScoped<IExtractionPersister, ExtractionPersister>();

        // ProviderProfileAggregator depends on the scoped DbContext as well.
        services.AddScoped<IProviderProfileAggregator, ProviderProfileAggregator>();

        return services;
    }

    /// <summary>
    /// Local-filesystem blob storage. Caller passes the absolute root path —
    /// typically <c>Path.Combine(IHostEnvironment.ContentRootPath, "blob-store")</c>
    /// from <c>Program.cs</c>, with an env-var override
    /// (<c>BLOB_STORE_ROOT</c>) for ops on a non-default mount.
    ///
    /// <para>P6 swaps this registration for an S3-backed implementation; consumers
    /// take <see cref="IBlobStore"/> and don't care.</para>
    /// </summary>
    public static IServiceCollection AddBlobStorage(
        this IServiceCollection services,
        string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Blob store root path is required.", nameof(rootPath));

        services.AddSingleton(new BlobStoreOptions { RootPath = rootPath });
        services.AddSingleton<IBlobStore, LocalFileBlobStore>();
        return services;
    }
}
