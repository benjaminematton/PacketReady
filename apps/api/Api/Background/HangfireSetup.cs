using Hangfire;
using Hangfire.PostgreSql;
using PacketReady.Infrastructure.Intake;
using PacketReady.Infrastructure.Outbox;

namespace PacketReady.Api.Background;

/// <summary>
/// Hangfire DI + dashboard + recurring job registration. Split out of
/// <c>Program.cs</c> so the WebHost stays tight; the configuration
/// surface is concentrated here.
///
/// <para><b>Storage.</b> Postgres-backed, same connection string as the
/// app's primary DbContext. Hangfire creates its own schema (<c>hangfire</c>)
/// inside the DB at startup — no separate migration needed.</para>
///
/// <para><b>Queues.</b> Two queues so a slow <see cref="IntakeTurnJob"/>
/// can't starve the <see cref="OutboxDispatcherJob"/>'s 30-second tick:
/// <list type="bullet">
///   <item><c>agent-turns</c> — bounded by the agent's per-turn wall-clock
///         budget; one slow turn shouldn't block dispatch.</item>
///   <item><c>outbox</c> — short-lived, must run every 30s.</item>
/// </list>
/// Queue order in <c>Queues</c> is priority — workers prefer queues
/// listed first when both have work. <c>outbox</c> goes first so its
/// short ticks aren't queued behind a long agent turn.</para>
///
/// <para><b>Dashboard.</b> Mounted at <c>/hangfire</c> in development
/// only. In other environments the dashboard is not mounted at all —
/// avoid shipping the enqueue/retry surface to the public internet
/// without an <see cref="Dashboard.IDashboardAuthorizationFilter"/>
/// wired into auth.</para>
/// </summary>
public static class HangfireSetup
{
    public const string AgentTurnsQueue = IntakeTurnJob.QueueName;
    public const string OutboxQueue = OutboxDispatcherJob.QueueName;

    public static IServiceCollection AddPacketReadyHangfire(
        this IServiceCollection services,
        string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException(
                "Hangfire requires a Postgres connection string.", nameof(connectionString));

        services.AddHangfire(cfg =>
        {
            cfg.UsePostgreSqlStorage(o => o.UseNpgsqlConnection(connectionString));
            cfg.UseSimpleAssemblyNameTypeSerializer();
            cfg.UseRecommendedSerializerSettings();
        });

        // Single server with both queues. Workers pull from queues in the
        // order listed — outbox first so its short ticks aren't blocked
        // behind a long agent turn. WorkerCount sized for ~3 concurrent
        // agent turns + 1 dedicated outbox worker on average.
        services.AddHangfireServer(options =>
        {
            options.WorkerCount = 4;
            options.Queues = new[] { OutboxQueue, AgentTurnsQueue };
        });

        return services;
    }

    /// <summary>
    /// Register the recurring outbox-drain job. Called from <c>Program.cs</c>
    /// after <c>app.Build()</c> — the recurring job manager is itself a
    /// scoped service that Hangfire surfaces once the host is up.
    /// </summary>
    public static IApplicationBuilder UsePacketReadyHangfire(this WebApplication app)
    {
        var recurring = app.Services.GetRequiredService<IRecurringJobManager>();
        recurring.AddOrUpdate<OutboxDispatcherJob>(
            recurringJobId: OutboxDispatcherJob.RecurringJobId,
            queue: OutboxQueue,
            methodCall: j => j.RunAsync(CancellationToken.None),
            cronExpression: OutboxDispatcherJob.DefaultCron);

        // Dashboard. Dev-only; other environments need
        // IDashboardAuthorizationFilter before the route is mounted.
        if (app.Environment.IsDevelopment())
        {
            app.UseHangfireDashboard("/hangfire");
        }

        return app;
    }
}
