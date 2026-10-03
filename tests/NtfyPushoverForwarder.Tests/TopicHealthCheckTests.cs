using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NtfyPushoverForwarder.Models;

namespace NtfyPushoverForwarder.Tests;

public class TopicHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_NoTopicsConfigured_ReturnsHealthy()
    {
        var tracker = new TopicConnectionTracker();
        var options = Options.Create(new ForwarderOptions { Topics = [] });
        var check = new TopicHealthCheck(tracker, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("No topic loops configured", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_NullTopicsConfigured_ReturnsHealthy()
    {
        var tracker = new TopicConnectionTracker();
        var options = Options.Create(new ForwarderOptions { Topics = null! });
        var check = new TopicHealthCheck(tracker, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_ConfiguredTopicsNoneConnected_ReturnsUnhealthy()
    {
        var tracker = new TopicConnectionTracker();
        var options = Options.Create(new ForwarderOptions { Topics = ["topic1", "topic2"] });
        var check = new TopicHealthCheck(tracker, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("0 of 2 topic loops active", result.Description);
        Assert.Contains("topic1", result.Description);
        Assert.Contains("topic2", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_ConfiguredTopicsAllConnected_ReturnsHealthy()
    {
        var tracker = new TopicConnectionTracker();
        tracker.MarkConnected("topic1");
        tracker.MarkConnected("topic2");
        var options = Options.Create(new ForwarderOptions { Topics = ["topic1", "topic2"] });
        var check = new TopicHealthCheck(tracker, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("All 2 topic loops active", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_SomeTopicsDisconnected_ReturnsDegraded()
    {
        var tracker = new TopicConnectionTracker();
        tracker.MarkConnected("topic1");
        // topic2 remains disconnected
        var options = Options.Create(new ForwarderOptions { Topics = ["topic1", "topic2"] });
        var check = new TopicHealthCheck(tracker, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("1 of 2 topic loops active", result.Description);
        Assert.Contains("topic2", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_TopicDisconnectsAfterConnection_ReportsUnhealthy()
    {
        var tracker = new TopicConnectionTracker();
        tracker.MarkConnected("topic1");
        var options = Options.Create(new ForwarderOptions { Topics = ["topic1"] });
        var check = new TopicHealthCheck(tracker, options);

        var healthyResult = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, healthyResult.Status);

        tracker.MarkDisconnected("topic1");

        var unhealthyResult = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, unhealthyResult.Status);
        Assert.Contains("0 of 1 topic loops active", unhealthyResult.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_TopicReconnects_ReportsHealthy()
    {
        var tracker = new TopicConnectionTracker();
        var options = Options.Create(new ForwarderOptions { Topics = ["topic1"] });
        var check = new TopicHealthCheck(tracker, options);

        tracker.MarkConnected("topic1");
        tracker.MarkDisconnected("topic1");
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);

        tracker.MarkConnected("topic1");
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    [Fact]
    public async Task EndToEnd_ViaHealthCheckService_FailsWhenTopicsStopped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ForwarderOptions>(opt =>
        {
            opt.Topics = ["alerts"];
        });
        services.AddSingleton<TopicConnectionTracker>();
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy("forwarder process up"))
            .AddCheck<TopicHealthCheck>("topics");

        var sp = services.BuildServiceProvider();
        var healthCheckService = sp.GetRequiredService<HealthCheckService>();

        // Initially no topic connected -> overall Unhealthy
        var report1 = await healthCheckService.CheckHealthAsync();
        Assert.Equal(HealthStatus.Unhealthy, report1.Status);
        Assert.Equal(HealthStatus.Healthy, report1.Entries["self"].Status);
        Assert.Equal(HealthStatus.Unhealthy, report1.Entries["topics"].Status);

        // Topic connects -> overall Healthy
        var tracker = sp.GetRequiredService<TopicConnectionTracker>();
        tracker.MarkConnected("alerts");

        var report2 = await healthCheckService.CheckHealthAsync();
        Assert.Equal(HealthStatus.Healthy, report2.Status);
        Assert.Equal(HealthStatus.Healthy, report2.Entries["topics"].Status);

        // Topic disconnects -> overall Unhealthy
        tracker.MarkDisconnected("alerts");

        var report3 = await healthCheckService.CheckHealthAsync();
        Assert.Equal(HealthStatus.Unhealthy, report3.Status);
        Assert.Equal(HealthStatus.Unhealthy, report3.Entries["topics"].Status);
    }
}
