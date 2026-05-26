using System.Text.Json.Serialization;
using MediatR;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PacketReady.Api.Endpoints;
using PacketReady.Application;
using PacketReady.Application.Ping;
using PacketReady.Application.Prompts;
using PacketReady.Infrastructure;
using PacketReady.Infrastructure.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// Kestrel's default MaxRequestBodySize (~28.6 MB) is lower than the per-endpoint
// RequestSizeLimit on /api/extract, so without this the endpoint attribute is
// silently truncated. Raise the server-wide ceiling to match the endpoint cap.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = ExtractEndpoint.MaxUploadBytes);

builder.Services.AddOpenApi();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Blob store roots at <content-root>/blob-store by default; ops can override
// via BLOB_STORE_ROOT to point at a mounted volume in containerized deploys.
// Relative paths resolve against the ASP.NET Core content root, not CWD —
// dotnet run from any directory still finds it.
var blobStoreRoot = builder.Configuration["BLOB_STORE_ROOT"];
var blobStoreAbsolutePath = string.IsNullOrWhiteSpace(blobStoreRoot)
    ? Path.Combine(builder.Environment.ContentRootPath, "blob-store")
    : Path.IsPathRooted(blobStoreRoot)
        ? blobStoreRoot
        : Path.Combine(builder.Environment.ContentRootPath, blobStoreRoot);
builder.Services.AddBlobStorage(blobStoreAbsolutePath);

// P5 outbox transport: file-writing mock SMTP. Same resolution rule as the
// blob store — env-var override, relative paths anchored at content root —
// so the demo loom's `.eml` files always land somewhere predictable.
var mockSmtpRoot = builder.Configuration["MOCK_SMTP_ROOT"];
var mockSmtpAbsolutePath = string.IsNullOrWhiteSpace(mockSmtpRoot)
    ? Path.Combine(builder.Environment.ContentRootPath, "outbox")
    : Path.IsPathRooted(mockSmtpRoot)
        ? mockSmtpRoot
        : Path.Combine(builder.Environment.ContentRootPath, mockSmtpRoot);
builder.Services.AddMockSmtp(mockSmtpAbsolutePath);
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(PingCommand).Assembly));
builder.Services.AddHostedService<PromptResourceValidator>();

// Serialize enums as strings (e.g. Tier.A, not 0). The default integer encoding
// is mystery-meat to any non-.NET consumer; this also keeps OpenAPI specs honest.
builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// CORS — dashboard runs on http://localhost:3001 in dev. Locked to one origin so a
// rogue local site can't hit the API. P5 will need a second origin for the intake
// portal; revisit then. Production deploys override via DASHBOARD_ORIGIN.
//
// Trim a trailing slash: browsers send the Origin header without one, so
// "http://foo/" in config silently fails every preflight match.
var dashboardOrigin = (builder.Configuration["DASHBOARD_ORIGIN"] ?? "http://localhost:3001")
    .TrimEnd('/');
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(dashboardOrigin)
    .AllowAnyHeader()
    .AllowAnyMethod()));

// OTel is always on. The exporter is optional — without LANGFUSE_OTEL_ENDPOINT we
// still register the ActivitySource so activity?.TraceId is populated and the response
// payload's trace id is meaningful in dev. The in-process tracer is effectively free.
var otlpEndpoint = builder.Configuration["LANGFUSE_OTEL_ENDPOINT"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName: "packetready-api"))
    .WithTracing(t =>
    {
        t.AddSource(LangfuseTelemetry.ActivitySourceName)
         .AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            t.AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri(otlpEndpoint);
                o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;

                var publicKey = builder.Configuration["LANGFUSE_PUBLIC_KEY"];
                var secretKey = builder.Configuration["LANGFUSE_SECRET_KEY"];
                if (!string.IsNullOrWhiteSpace(publicKey) && !string.IsNullOrWhiteSpace(secretKey))
                {
                    var basic = Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}"));
                    o.Headers = $"Authorization=Basic {basic}";
                }
            });
        }
    });

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// CORS before endpoint mapping — ASP.NET Core's middleware order is real.
app.UseCors();

app.MapPingEndpoint();
app.MapProviderEndpoints();
app.MapScoreEndpoints();
app.MapExtractEndpoint();
app.MapDocumentEndpoints();
app.Run();

public partial class Program;
