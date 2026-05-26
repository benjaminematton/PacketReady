using global::Hangfire;
using global::Hangfire.PostgreSql;
using PacketReady.Infrastructure.Outbox;

namespace PacketReady.Api.Hangfire;

/// <summary>
/// Hangfire DI + dashboard + recurring job registration. Split out of
/// <c>Program.cs</c> so the WebHost stays tight; the configuration
/// surface is concentrated here.
///
/// <para><b>Storage.</b> Postgres-backed, same connection string as the
/// app's primary DbContext. Hangfire creates its own schema (<c>hangfire</c>)
/// inside the DB at startup — no separate migration needed.</para>
///
/// <para><b>Dashboard.</b> Mounted at <c>/hangfire</c> in development.
/// Locked off in production until a real auth filter is wired — the
/// dashboard would otherwise expose enqueue + retry buttons to anyone
/// reaching the route.</para>
/// </summary>
public static class HangfireSetup
{
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

        services.AddHangfireServer(options =>
        {
            options.WorkerCount = 4;
            options.Queues = new[] { "default" };
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
            methodCall: j => j.RunAsync(CancellationToken.None),
            cronExpression: OutboxDispatcherJob.DefaultCron);

        // Dashboard. Dev-only; prod needs IDashboardAuthorizationFilter.
        if (app.Environment.IsDevelopment())
        {
            app.UseHangfireDashboard("/hangfire");
        }

        return app;
    }
}
