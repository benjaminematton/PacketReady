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
using PacketReady.Application.Intake.Agent;
using PacketReady.Application.Intake.Agent.Tools;
using PacketReady.Application.Intake.MagicLinks;
using PacketReady.Application.Intake.Outbox;
using PacketReady.Application.Intake.PrimarySources;
using PacketReady.Infrastructure.Audit;
using PacketReady.Infrastructure.Blob;
using PacketReady.Infrastructure.Extraction;
using PacketReady.Infrastructure.Extraction.Classifier;
using PacketReady.Infrastructure.Extraction.SonnetExtractors;
using PacketReady.Application.Nucc;
using PacketReady.Application.Payers;
using PacketReady.Infrastructure.Intake;
using PacketReady.Infrastructure.MagicLinks;
using PacketReady.Infrastructure.PrimarySources;
using PacketReady.Infrastructure.Nucc;
using PacketReady.Infrastructure.Outbox;
using PacketReady.Infrastructure.Payers;
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

        // Postgres-shaped DbUpdateException translator. Lets Application
        // handlers detect unique-violation races (StartIntake's TOCTOU,
        // ExtractionPersister's idempotency race) without referencing
        // Npgsql.PostgresException directly. Stateless; singleton.
        services.AddSingleton<IDbExceptionTranslator, PostgresExceptionTranslator>();

        // Per-payer requirements behind the IPayerCatalog seam (see
        // Application/Payers/IPayerCatalog.cs for rationale: centralizes the
        // unknown-payer error so the API can map it to a 4xx via
        // PayerNotConfiguredException). PayerRequirementLoader fails loud on
        // schema violation so the app refuses to start with a broken YAML —
        // better than a NullReferenceException mid-request. Registered in
        // AddPersistence (not AddInfrastructure) so the seed CLI and any
        // future LLM-free binary can resolve the catalog without dragging in
        // the Anthropic SDK.
        var payerDir = Path.Combine(AppContext.BaseDirectory, "Payers", "payers");
        var payerDict = PayerRequirementLoader.LoadAll(payerDir);
        services.AddSingleton<IPayerCatalog>(new PayerCatalog(payerDict));

        // NUCC taxonomy snapshot. Same lifetime pattern as the payer dict:
        // loaded once at DI bootstrap and held as a singleton; fails loud
        // on a missing or malformed CSV so the app refuses to start with a
        // broken snapshot. Bumping NUCC's biannual release is a filename
        // edit here + a fresh CSV under data/.
        var nuccPath = Path.Combine(AppContext.BaseDirectory, "Nucc", "nucc-taxonomy-25.1.csv");
        services.AddSingleton<INuccTaxonomyLookup>(new NuccTaxonomyLookup(nuccPath));

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
        services.AddScoped<IProviderProfileAggregator, ProviderProfileAggregator>();

        // P5 intake agent. The 5 tools are scoped because they touch the
        // scoped DbContext / IMediator / ComposeFollowupHandler; the agent
        // itself is scoped so it shares the request's IChatClient + tool
        // instances. The Application port (IIntakeAgent) is what
        // IntakeTurnJob (C5) takes — runtime swaps via DI replacement when
        // a different model / harness is needed.
        services.AddSingleton<IPrimarySourceLookup, MockPrimarySourceLookup>();
        services.AddScoped<ComposeFollowupHandler>();
        services.AddScoped<IIntakeTool, ReadDocumentTool>();
        services.AddScoped<IIntakeTool, ExtractFieldsTool>();
        services.AddScoped<IIntakeTool, LookupPrimarySourceTool>();
        services.AddScoped<IIntakeTool, ComposeFollowupTool>();
        services.AddScoped<IIntakeTool, ComputeReadinessTool>();
        services.AddScoped<IIntakeAgent, IntakeAgent>();

        // P5 C5 orchestration: IntakeTurnJob is enqueued on portal submit
        // (one job per submit), OutboxDispatcherJob runs every 30s
        // (recurring). The transitioner is the pure-code glue that maps
        // AgentTurnResult onto the FSM transition + side effects. All
        // scoped because they take the request's IAppDbContext.
        services.AddScoped<IntakeStateTransitioner>();
        services.AddScoped<IntakeTurnJob>();
        services.AddScoped<OutboxDispatcherJob>();

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

    /// <summary>
    /// File-writing mock SMTP for the P5 demo. Same shape as
    /// <see cref="AddBlobStorage"/> — caller resolves the absolute root path
    /// from the API content root + an env-var override
    /// (<c>MOCK_SMTP_ROOT</c>). P6+ swaps this registration for a real
    /// <see cref="IEmailSender"/> impl; consumers stay on the port.
    /// </summary>
    public static IServiceCollection AddMockSmtp(
        this IServiceCollection services,
        string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Mock SMTP root path is required.", nameof(rootPath));

        services.AddSingleton(new MockSmtpOptions { RootPath = rootPath });
        services.AddSingleton<IEmailSender, MockSmtpSender>();
        return services;
    }

    /// <summary>
    /// Magic-link signer + validator. Caller passes the HMAC signing key
    /// — typically <c>config["MAGIC_LINK_SIGNING_KEY"]</c> from
    /// <c>Program.cs</c>. The issuer is scoped because it holds the
    /// scoped <c>IAppDbContext</c>; the secret rides on the options
    /// singleton.
    ///
    /// <para>Rotating the signing key invalidates outstanding magic
    /// links — flagged in phase-5-intake-agent.md "Risks / open."</para>
    /// </summary>
    public static IServiceCollection AddMagicLinks(
        this IServiceCollection services,
        string signingKey)
    {
        if (string.IsNullOrWhiteSpace(signingKey))
            throw new ArgumentException(
                "MAGIC_LINK_SIGNING_KEY is required.", nameof(signingKey));

        services.AddSingleton(new MagicLinkOptions { SigningKey = signingKey });
        services.AddScoped<IMagicLinkAuthority, MagicLinkIssuer>();
        return services;
    }
}
