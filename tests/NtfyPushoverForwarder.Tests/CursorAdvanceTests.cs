using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NtfyPushoverForwarder;
using NtfyPushoverForwarder.Models;

namespace NtfyPushoverForwarder.Tests;

public class CursorAdvanceTests
{
    private class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public TestHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options.Name, options.Version);
        public void Dispose() { }
    }

    private static (Worker Worker, TopicConnectionTracker Connections) Create(
        ForwarderOptions options,
        HttpStatusCode pushoverStatus)
    {
        var connections = new TopicConnectionTracker();
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(pushoverStatus)
        {
            Content = new StringContent(pushoverStatus == HttpStatusCode.OK ? "{\"status\":1}" : "{\"status\":0}")
        });
        var worker = new Worker(
            NullLogger<Worker>.Instance,
            new TestHttpClientFactory(handler),
            Options.Create(options),
            new ForwarderMetrics(new TestMeterFactory()),
            connections,
            new InMemoryDedupeStore());
        return (worker, connections);
    }

    private static ForwarderOptions BaseOptions() => new()
    {
        NtfyUrl = "http://ntfy.example.test",
        PushoverUrl = "http://pushover.example.test/1/messages.json",
        PushoverUserKey = "REDACTED_TEST_VALUE",
        PushoverDefaultToken = "REDACTED_TEST_VALUE",
        MinimumPriority = 0,
        DeduplicationWindowSeconds = 60,
        DeduplicationMaxEntries = 100,
        Topics = new[] { "alerts" },
    };

    private static NtfyMessage Msg(string id, long time, int? priority = 3) => new()
    {
        Id = id,
        Time = time,
        Event = "message",
        Topic = "alerts",
        Title = "test",
        Message = "hello",
        Priority = priority,
    };

    [Fact]
    public async Task HandleMessageAsync_FailedPushover_DoesNotAdvanceCursor()
    {
        var (worker, connections) = Create(BaseOptions(), HttpStatusCode.BadRequest);
        await worker.HandleMessageAsync("alerts", Msg("msg-fail", 1001), CancellationToken.None);
        Assert.Null(connections.GetSinceCursor("alerts"));
    }

    [Fact]
    public async Task HandleMessageAsync_SuccessfulPushover_AdvancesCursor()
    {
        var (worker, connections) = Create(BaseOptions(), HttpStatusCode.OK);
        await worker.HandleMessageAsync("alerts", Msg("msg-ok", 1002), CancellationToken.None);
        Assert.Equal("msg-ok", connections.GetSinceCursor("alerts"));
    }

    [Fact]
    public async Task HandleMessageAsync_PriorityDrop_AdvancesCursor()
    {
        var options = BaseOptions();
        options.MinimumPriority = 4; // drop priority 3
        var (worker, connections) = Create(options, HttpStatusCode.BadRequest);
        await worker.HandleMessageAsync("alerts", Msg("msg-low", 1003, priority: 1), CancellationToken.None);
        Assert.Equal("msg-low", connections.GetSinceCursor("alerts"));
    }
}
