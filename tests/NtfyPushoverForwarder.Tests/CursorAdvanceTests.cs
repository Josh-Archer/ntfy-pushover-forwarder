using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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

    private sealed class BlockingPushoverHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => _calls;
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}")
            };
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

    private sealed class TestCallbackLogger<T> : ILogger<T>
    {
        private readonly Action<LogLevel, string> _callback;

        public TestCallbackLogger(Action<LogLevel, string> callback)
        {
            _callback = callback;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter != null ? formatter(state, exception) : state?.ToString() ?? string.Empty;
            _callback(logLevel, message);
        }
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

    private static NtfyMessage Msg(string id, long time, int? priority = 3, string message = "hello", string title = "test") => new()
    {
        Id = id,
        Time = time,
        Event = "message",
        Topic = "alerts",
        Title = title,
        Message = message,
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
    public async Task ForwardToPushoverAsync_ConcurrentDuplicate_IsSentOnlyOnce()
    {
        var handler = new BlockingPushoverHandler();
        var worker = new Worker(
            NullLogger<Worker>.Instance,
            new TestHttpClientFactory(handler),
            Options.Create(BaseOptions()),
            new ForwarderMetrics(new TestMeterFactory()),
            new TopicConnectionTracker(),
            new InMemoryDedupeStore());

        var first = worker.ForwardToPushoverAsync("alerts", Msg("same-id", 1001), CancellationToken.None);
        await handler.Entered;
        var second = await worker.ForwardToPushoverAsync("alerts", Msg("same-id", 1001), CancellationToken.None);

        handler.Release();
        var firstResult = await first;

        Assert.Equal(ForwardResult.TransientFailure, second);
        Assert.Equal(ForwardResult.Success, firstResult);
        Assert.Equal(1, handler.Calls);
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

    private static (Worker Worker, TopicConnectionTracker Connections, IDedupeStore DedupeStore) CreateWithHandler(
        ForwarderOptions options,
        Func<HttpRequestMessage, HttpResponseMessage> handlerFunc,
        IDedupeStore? dedupeStore = null,
        TopicConnectionTracker? connections = null,
        ILogger<Worker>? logger = null)
    {
        var tracker = connections ?? new TopicConnectionTracker();
        var store = dedupeStore ?? new InMemoryDedupeStore();
        var worker = new Worker(
            logger ?? NullLogger<Worker>.Instance,
            new TestHttpClientFactory(new TestHttpMessageHandler(handlerFunc)),
            Options.Create(options),
            new ForwarderMetrics(new TestMeterFactory()),
            tracker,
            store);
        return (worker, tracker, store);
    }

    private static HttpResponseMessage JsonSseResponse(params NtfyMessage[] messages)
    {
        var lines = messages.Select(m => JsonSerializer.Serialize(m));
        var text = string.Join("\n", lines) + "\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(text)))
        };
    }

    [Fact]
    public async Task HandleMessageAsync_FailedMessageFollowedBySuccessfulMessage_DoesNotAdvanceCursor()
    {
        var options = BaseOptions();
        var (worker, connections, _) = CreateWithHandler(options, req =>
        {
            var content = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            if (content.Contains("fail-message"))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"status\":0}")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}")
            };
        });

        await worker.HandleMessageAsync("alerts", Msg("msg-fail", 1001, message: "fail-message"), CancellationToken.None);
        await worker.HandleMessageAsync("alerts", Msg("successful-B", 1002, message: "b-message"), CancellationToken.None);

        Assert.Null(connections.GetSinceCursor("alerts"));
    }

    [Fact]
    public async Task HandleMessageAsync_FirstMessageFailure_ReconnectReplayAndDuplicateSuppression()
    {
        var options = BaseOptions();
        int bPostCount = 0;
        bool failA = true;

        var (worker, connections, _) = CreateWithHandler(options, req =>
        {
            var content = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            if (content.Contains("msg-A-body"))
            {
                if (failA)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("{\"status\":0}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":1}")
                };
            }

            if (content.Contains("msg-B-body"))
            {
                bPostCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":1}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}")
            };
        });

        // Step 1: First message A fails
        await worker.HandleMessageAsync("alerts", Msg("msg-A", 1001, message: "msg-A-body"), CancellationToken.None);
        Assert.Null(connections.GetSinceCursor("alerts"));

        // Step 2: Message B succeeds
        await worker.HandleMessageAsync("alerts", Msg("successful-B", 1002, message: "msg-B-body"), CancellationToken.None);
        // Safe replay cursor CANNOT advance past A
        Assert.Null(connections.GetSinceCursor("alerts"));
        Assert.Equal(1, bPostCount);

        // Step 3: Reconnect / replay
        // Verify subscribe URL for reconnect from safe cursor (which is null)
        var reconnectUrl = SseReconnectPolicy.BuildSubscribeUrl(options.NtfyUrl, "alerts", connections.GetSinceCursor("alerts"));
        Assert.Equal("http://ntfy.example.test/alerts/json", reconnectUrl);

        // Replay message A (now succeeding)
        failA = false;
        await worker.HandleMessageAsync("alerts", Msg("msg-A", 1001, message: "msg-A-body"), CancellationToken.None);
        // Cursor now advances to msg-A
        Assert.Equal("msg-A", connections.GetSinceCursor("alerts"));

        // Replay message B (duplicate suppression)
        await worker.HandleMessageAsync("alerts", Msg("successful-B", 1002, message: "msg-B-body"), CancellationToken.None);
        // Duplicate suppressed: Pushover was not posted again for B
        Assert.Equal(1, bPostCount);
        // Cursor advances to B
        Assert.Equal("successful-B", connections.GetSinceCursor("alerts"));
    }

    [Fact]
    public async Task HandleMessageAsync_SubsequentMessageFailure_DoesNotAdvancePastFailure()
    {
        var options = BaseOptions();
        bool failA = true;
        var (worker, connections, _) = CreateWithHandler(options, req =>
        {
            var content = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            if (content.Contains("body-A") && failA)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"status\":0}")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}")
            };
        });

        // Msg 0 succeeds
        await worker.HandleMessageAsync("alerts", Msg("msg-0", 1000, message: "body-0"), CancellationToken.None);
        Assert.Equal("msg-0", connections.GetSinceCursor("alerts"));

        // Msg A fails
        await worker.HandleMessageAsync("alerts", Msg("msg-A", 1001, message: "body-A"), CancellationToken.None);
        Assert.Equal("msg-0", connections.GetSinceCursor("alerts"));

        // Msg B succeeds
        await worker.HandleMessageAsync("alerts", Msg("msg-B", 1002, message: "body-B"), CancellationToken.None);
        // Safe cursor remains msg-0, does not advance to msg-B
        Assert.Equal("msg-0", connections.GetSinceCursor("alerts"));

        // Replay from msg-0
        var reconnectUrl = SseReconnectPolicy.BuildSubscribeUrl(options.NtfyUrl, "alerts", connections.GetSinceCursor("alerts"));
        Assert.Equal("http://ntfy.example.test/alerts/json?since=msg-0", reconnectUrl);

        failA = false;
        await worker.HandleMessageAsync("alerts", Msg("msg-A", 1001, message: "body-A"), CancellationToken.None);
        Assert.Equal("msg-A", connections.GetSinceCursor("alerts"));

        await worker.HandleMessageAsync("alerts", Msg("msg-B", 1002, message: "body-B"), CancellationToken.None);
        Assert.Equal("msg-B", connections.GetSinceCursor("alerts"));
    }

    [Fact]
    public async Task HandleMessageAsync_BoundedRetries_SucceedsOnTransientRetry()
    {
        var options = BaseOptions();
        options.MaxRetries = 3;
        int attempts = 0;
        var (worker, connections, _) = CreateWithHandler(options, _ =>
        {
            attempts++;
            if (attempts < 3)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"status\":0}")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}")
            };
        });

        await worker.HandleMessageAsync("alerts", Msg("msg-retry", 1001), CancellationToken.None);
        Assert.Equal(3, attempts);
        Assert.Equal("msg-retry", connections.GetSinceCursor("alerts"));
    }

    [Fact]
    public async Task HandleMessageAsync_BoundedRetries_ExhaustsRetriesOnTransientFailure()
    {
        var options = BaseOptions();
        options.MaxRetries = 3;
        int attempts = 0;
        var (worker, connections, _) = CreateWithHandler(options, _ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"status\":0}")
            };
        });

        var handled = await worker.HandleMessageAsync("alerts", Msg("msg-fail", 1001), CancellationToken.None);
        Assert.False(handled);
        Assert.Equal(3, attempts);
        Assert.Null(connections.GetSinceCursor("alerts"));
    }

    [Fact]
    public async Task HandleMessageAsync_PermanentlyInvalidMessage_DoesNotRetryAndDoesNotBlockStream()
    {
        var options = BaseOptions();
        options.MaxRetries = 3;
        int aAttempts = 0;
        var (worker, connections, _) = CreateWithHandler(options, req =>
        {
            var content = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            if (content.Contains("invalid-body"))
            {
                aAttempts++;
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"status\":0,\"errors\":[\"bad parameter\"]}")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}")
            };
        });

        // Message A is permanently invalid (400)
        var handledA = await worker.HandleMessageAsync("alerts", Msg("msg-invalid", 1001, message: "invalid-body"), CancellationToken.None);
        Assert.True(handledA);
        // Only 1 attempt made: bounded retries are NOT wasted on permanently invalid messages
        Assert.Equal(1, aAttempts);
        // Cursor remains null (the failed message itself did not advance cursor)
        Assert.Null(connections.GetSinceCursor("alerts"));

        // Message B succeeds
        var handledB = await worker.HandleMessageAsync("alerts", Msg("msg-valid", 1002, message: "valid-body"), CancellationToken.None);
        Assert.True(handledB);
        // Cursor advances to msg-valid (permanently invalid message does not block stream forever)
        Assert.Equal("msg-valid", connections.GetSinceCursor("alerts"));
    }

    [Fact]
    public async Task HandleMessageAsync_MessageWithEmptyId_IsPermanentlyInvalidAndDoesNotBlockStream()
    {
        var options = BaseOptions();
        var (worker, connections) = Create(options, HttpStatusCode.OK);

        // Message with empty ID
        await worker.HandleMessageAsync("alerts", Msg("", 1001), CancellationToken.None);
        Assert.Null(connections.GetSinceCursor("alerts"));

        // Subsequent message with valid ID
        await worker.HandleMessageAsync("alerts", Msg("msg-valid", 1002), CancellationToken.None);
        Assert.Equal("msg-valid", connections.GetSinceCursor("alerts"));
    }

    [Fact]
    public async Task HandleMessageAsync_MultipleFailuresInSequence_ResolvedInOrder()
    {
        var options = BaseOptions();
        var failedIds = new HashSet<string> { "msg-1", "msg-2" };

        var (worker, connections, _) = CreateWithHandler(options, req =>
        {
            var content = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            foreach (var failId in failedIds)
            {
                if (content.Contains(failId))
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("{\"status\":0}")
                    };
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}")
            };
        });

        // msg-1 fails, msg-2 fails, msg-3 succeeds
        await worker.HandleMessageAsync("alerts", Msg("msg-1", 1001, message: "body-msg-1"), CancellationToken.None);
        await worker.HandleMessageAsync("alerts", Msg("msg-2", 1002, message: "body-msg-2"), CancellationToken.None);
        await worker.HandleMessageAsync("alerts", Msg("msg-3", 1003, message: "body-msg-3"), CancellationToken.None);

        Assert.Null(connections.GetSinceCursor("alerts"));

        // Replay msg-1: succeeds
        failedIds.Remove("msg-1");
        await worker.HandleMessageAsync("alerts", Msg("msg-1", 1001, message: "body-msg-1"), CancellationToken.None);
        // Cursor advances to msg-1 because msg-1 is resolved, but msg-2 is still unresolved
        Assert.Equal("msg-1", connections.GetSinceCursor("alerts"));

        // Replay msg-2: still fails
        await worker.HandleMessageAsync("alerts", Msg("msg-2", 1002, message: "body-msg-2"), CancellationToken.None);
        Assert.Equal("msg-1", connections.GetSinceCursor("alerts"));

        // Replay msg-3: deduped, but cannot advance past unresolved msg-2
        await worker.HandleMessageAsync("alerts", Msg("msg-3", 1003, message: "body-msg-3"), CancellationToken.None);
        Assert.Equal("msg-1", connections.GetSinceCursor("alerts"));

        // Next reconnect: replay msg-2 now succeeds
        failedIds.Remove("msg-2");
        await worker.HandleMessageAsync("alerts", Msg("msg-2", 1002, message: "body-msg-2"), CancellationToken.None);
        Assert.Equal("msg-2", connections.GetSinceCursor("alerts"));

        // Replay msg-3: deduped, advances to msg-3
        await worker.HandleMessageAsync("alerts", Msg("msg-3", 1003, message: "body-msg-3"), CancellationToken.None);
        Assert.Equal("msg-3", connections.GetSinceCursor("alerts"));
    }

    [Fact]
    public async Task ListenToTopicAsync_TransientFailure_CausesReconnectFromCursorBeforeFailureAndRetrySucceeds()
    {
        var options = BaseOptions();
        options.MaxRetries = 1;
        options.RetryDelayMilliseconds = 0;
        options.ReconnectInitialDelayMilliseconds = 1;
        options.SendRecoveryAlerts = false;

        var connections = new TopicConnectionTracker();
        // Seed safe cursor before failure: msg-0 already succeeded
        connections.NoteMessage("alerts", "msg-0", 1000);
        Assert.Equal("msg-0", connections.GetSinceCursor("alerts"));

        var requestedNtfyUrls = new List<string>();
        int pushoverAttemptsA = 0;
        int pushoverAttemptsB = 0;
        bool shouldFailA = true;

        var (worker, _, _) = CreateWithHandler(options, req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (req.RequestUri?.Host == "ntfy.example.test")
            {
                lock (requestedNtfyUrls)
                {
                    requestedNtfyUrls.Add(uri);
                    if (requestedNtfyUrls.Count >= 2)
                    {
                        shouldFailA = false;
                    }
                }

                if (requestedNtfyUrls.Count > 2)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new MemoryStream())
                    };
                }

                // Both attempts provide msg-A followed by msg-B
                return JsonSseResponse(
                    Msg("msg-A", 1001, message: "body-A"),
                    Msg("msg-B", 1002, message: "body-B")
                );
            }

            if (req.RequestUri?.Host == "pushover.example.test")
            {
                var content = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                if (content.Contains("body-A"))
                {
                    pushoverAttemptsA++;
                    if (shouldFailA)
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        {
                            Content = new StringContent("{\"status\":0}")
                        };
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"status\":1}")
                    };
                }

                if (content.Contains("body-B"))
                {
                    pushoverAttemptsB++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"status\":1}")
                    };
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}")
            };
        }, connections: connections);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listenTask = worker.ListenToTopicAsync("alerts", cts.Token);

        // Wait until retry of msg-A succeeds and msg-B also succeeds
        while (!cts.IsCancellationRequested)
        {
            if (connections.GetSinceCursor("alerts") == "msg-B")
            {
                break;
            }
            await Task.Delay(5);
        }

        cts.Cancel();
        try
        {
            await listenTask;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(requestedNtfyUrls.Count >= 2);
        // Initial connection was from safe cursor msg-0
        Assert.Equal("http://ntfy.example.test/alerts/json?since=msg-0", requestedNtfyUrls[0]);
        // Reconnect after transient failure on msg-A must reconnect from safe cursor msg-0 (before the failure)
        Assert.Equal("http://ntfy.example.test/alerts/json?since=msg-0", requestedNtfyUrls[1]);
        // msg-B was NOT processed in stream 1 because transient failure on msg-A stopped reading stream 1
        Assert.Equal(1, pushoverAttemptsB);
        // msg-A was retried on stream 2 and succeeded
        Assert.Equal(2, pushoverAttemptsA);
        // Safe cursor advanced through msg-A to msg-B
        Assert.Equal("msg-B", connections.GetSinceCursor("alerts"));
    }

    [Fact]
    public async Task ListenToTopicAsync_FirstMessageFailure_CausesReconnectWithoutCursorAndRetrySucceeds()
    {
        var options = BaseOptions();
        options.MaxRetries = 1;
        options.RetryDelayMilliseconds = 0;
        options.ReconnectInitialDelayMilliseconds = 1;
        options.SendRecoveryAlerts = false;

        var connections = new TopicConnectionTracker();
        Assert.Null(connections.GetSinceCursor("alerts"));

        var requestedNtfyUrls = new List<string>();
        int pushoverAttempts1 = 0;
        int pushoverAttempts2 = 0;
        bool shouldFail1 = true;

        var (worker, _, _) = CreateWithHandler(options, req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (req.RequestUri?.Host == "ntfy.example.test")
            {
                lock (requestedNtfyUrls)
                {
                    requestedNtfyUrls.Add(uri);
                    if (requestedNtfyUrls.Count >= 2)
                    {
                        shouldFail1 = false;
                    }
                }

                if (requestedNtfyUrls.Count > 2)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new MemoryStream())
                    };
                }

                return JsonSseResponse(
                    Msg("msg-1", 1001, message: "first-msg"),
                    Msg("msg-2", 1002, message: "second-msg")
                );
            }

            if (req.RequestUri?.Host == "pushover.example.test")
            {
                var content = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                if (content.Contains("first-msg"))
                {
                    pushoverAttempts1++;
                    if (shouldFail1)
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        {
                            Content = new StringContent("{\"status\":0}")
                        };
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"status\":1}")
                    };
                }

                if (content.Contains("second-msg"))
                {
                    pushoverAttempts2++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"status\":1}")
                    };
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}")
            };
        }, connections: connections);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listenTask = worker.ListenToTopicAsync("alerts", cts.Token);

        while (!cts.IsCancellationRequested)
        {
            if (connections.GetSinceCursor("alerts") == "msg-2")
            {
                break;
            }
            await Task.Delay(5);
        }

        cts.Cancel();
        try
        {
            await listenTask;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(requestedNtfyUrls.Count >= 2);
        // First message failure: safe cursor remains null
        Assert.Equal("http://ntfy.example.test/alerts/json", requestedNtfyUrls[0]);
        // Reconnect must not include a since query param
        Assert.Equal("http://ntfy.example.test/alerts/json", requestedNtfyUrls[1]);
        Assert.Equal(1, pushoverAttempts2);
        Assert.Equal(2, pushoverAttempts1);
        Assert.Equal("msg-2", connections.GetSinceCursor("alerts"));
    }

    [Fact]
    public async Task ListenToTopicAsync_Replay_SuppressesDuplicates()
    {
        var options = BaseOptions();
        options.MaxRetries = 1;
        options.RetryDelayMilliseconds = 0;
        options.ReconnectInitialDelayMilliseconds = 1;
        options.SendRecoveryAlerts = false;

        var connections = new TopicConnectionTracker();
        var dedupeStore = new InMemoryDedupeStore();
        var requestedNtfyUrls = new List<string>();
        int pushoverAttemptsA = 0;
        int pushoverAttemptsB = 0;
        bool shouldFailB = true;

        var (worker, _, _) = CreateWithHandler(options, req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (req.RequestUri?.Host == "ntfy.example.test")
            {
                lock (requestedNtfyUrls)
                {
                    requestedNtfyUrls.Add(uri);
                    if (requestedNtfyUrls.Count >= 2)
                    {
                        shouldFailB = false;
                    }
                }

                if (requestedNtfyUrls.Count > 2)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new MemoryStream())
                    };
                }

                // Both stream 1 and stream 2 deliver msg-A and msg-B
                return JsonSseResponse(
                    Msg("msg-A", 1001, message: "msg-A-body"),
                    Msg("msg-B", 1002, message: "msg-B-body")
                );
            }

            if (req.RequestUri?.Host == "pushover.example.test")
            {
                var content = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                if (content.Contains("msg-A-body"))
                {
                    pushoverAttemptsA++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"status\":1}")
                    };
                }

                if (content.Contains("msg-B-body"))
                {
                    pushoverAttemptsB++;
                    if (shouldFailB)
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        {
                            Content = new StringContent("{\"status\":0}")
                        };
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"status\":1}")
                    };
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}")
            };
        }, connections: connections, dedupeStore: dedupeStore);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listenTask = worker.ListenToTopicAsync("alerts", cts.Token);

        while (!cts.IsCancellationRequested)
        {
            if (connections.GetSinceCursor("alerts") == "msg-B")
            {
                break;
            }
            await Task.Delay(5);
        }

        cts.Cancel();
        try
        {
            await listenTask;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(requestedNtfyUrls.Count >= 2);
        // Initial stream URL
        Assert.Equal("http://ntfy.example.test/alerts/json", requestedNtfyUrls[0]);
        // Reconnect stream URL from safe cursor msg-A (before the failed msg-B)
        Assert.Equal("http://ntfy.example.test/alerts/json?since=msg-A", requestedNtfyUrls[1]);
        // msg-A was replayed in stream 2, but duplicate was suppressed so Pushover was only called once!
        Assert.Equal(1, pushoverAttemptsA);
        // msg-B failed on stream 1 and succeeded on stream 2
        Assert.Equal(2, pushoverAttemptsB);
        // Safe cursor advanced to msg-B
        Assert.Equal("msg-B", connections.GetSinceCursor("alerts"));
    }

    [Fact]
    public async Task ListenToTopicAsync_PermanentFailure_DoesNotStopStreamAndAllowsSubsequentSuccessToAdvanceCursor()
    {
        var options = BaseOptions();
        options.MaxRetries = 1;
        options.RetryDelayMilliseconds = 0;
        options.ReconnectInitialDelayMilliseconds = 60_000;
        options.SendRecoveryAlerts = false;

        var connections = new TopicConnectionTracker();
        var requestedNtfyUrls = new List<string>();
        int pushoverAttemptsA = 0;
        int pushoverAttemptsB = 0;
        var streamEndedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var logger = new TestCallbackLogger<Worker>((logLevel, message) =>
        {
            if (message.Contains("SSE stream ended"))
            {
                streamEndedTcs.TrySetResult(true);
            }
        });

        var (worker, _, _) = CreateWithHandler(options, req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (req.RequestUri?.Host == "ntfy.example.test")
            {
                lock (requestedNtfyUrls)
                {
                    requestedNtfyUrls.Add(uri);
                }

                // Stream contains permanently invalid message followed by valid message
                return JsonSseResponse(
                    Msg("msg-invalid", 1001, message: "bad-body"),
                    Msg("msg-valid", 1002, message: "good-body")
                );
            }

            if (req.RequestUri?.Host == "pushover.example.test")
            {
                var content = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                if (content.Contains("bad-body"))
                {
                    pushoverAttemptsA++;
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"status\":0,\"errors\":[\"bad request\"]}")
                    };
                }

                if (content.Contains("good-body"))
                {
                    pushoverAttemptsB++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"status\":1}")
                    };
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}")
            };
        }, connections: connections, logger: logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listenTask = worker.ListenToTopicAsync("alerts", cts.Token);

        await streamEndedTcs.Task.WaitAsync(cts.Token);

        cts.Cancel();
        try
        {
            await listenTask;
        }
        catch (OperationCanceledException)
        {
        }

        // Permanent failure did NOT stop stream or cause reconnect
        Assert.Single(requestedNtfyUrls);
        Assert.Equal(1, pushoverAttemptsA);
        Assert.Equal(1, pushoverAttemptsB);
        // Safe cursor advanced to msg-valid despite earlier permanent failure
        Assert.Equal("msg-valid", connections.GetSinceCursor("alerts"));
    }
}

