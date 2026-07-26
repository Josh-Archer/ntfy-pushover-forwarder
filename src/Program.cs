using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NtfyPushoverForwarder;
using NtfyPushoverForwarder.Models;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<ForwarderOptions>(builder.Configuration.GetSection("Forwarder"));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TopicConnectionTracker>();
builder.Services.AddSingleton<ForwarderMetrics>();
builder.Services.AddSingleton<IDedupeStore>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ForwarderOptions>>().Value;
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DedupeStore");
    var window = TimeSpan.FromSeconds(options.DeduplicationWindowSeconds);
    if (!string.IsNullOrWhiteSpace(options.DeduplicationStorePath))
    {
        return new FileDedupeStore(options.DeduplicationStorePath, window, options.DeduplicationMaxEntries, logger);
    }

    return new InMemoryDedupeStore();
});
builder.Services.AddHostedService<Worker>();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy("forwarder process up"));

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(ForwarderMetrics.MeterName);
        metrics.AddPrometheusExporter();
        metrics.AddRuntimeInstrumentation();
    });

var optionsPreview = builder.Configuration.GetSection("Forwarder").Get<ForwarderOptions>() ?? new ForwarderOptions();
var port = optionsPreview.HttpPort > 0 ? optionsPreview.HttpPort : 8080;
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description
            })
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
});

app.MapPrometheusScrapingEndpoint("/metrics");

app.Logger.LogInformation("HTTP surface listening on port {Port} (/healthz, /metrics)", port);

await app.RunAsync();

// Expose for WebApplicationFactory tests if added later
public partial class Program;
