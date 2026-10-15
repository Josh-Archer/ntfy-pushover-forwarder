using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NtfyPushoverForwarder.Models;

namespace NtfyPushoverForwarder;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ForwarderOptions _options;
    private readonly ForwarderMetrics _metrics;
    private readonly TopicConnectionTracker _connections;
    private readonly IDedupeStore _dedupeStore;
    private readonly Dictionary<string, byte[]> _iconCache = new();

    public Worker(
        ILogger<Worker> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<ForwarderOptions> options,
        ForwarderMetrics metrics,
        TopicConnectionTracker connections,
        IDedupeStore dedupeStore)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _metrics = metrics;
        _connections = connections;
        _dedupeStore = dedupeStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting NtfyPushoverForwarder");

        if (string.IsNullOrEmpty(_options.NtfyUrl))
        {
            _logger.LogError("NtfyUrl is not configured.");
            return;
        }

        if (_options.Topics == null || _options.Topics.Length == 0)
        {
            _logger.LogWarning("No topics configured to listen to.");
            return;
        }

        var tasks = _options.Topics.Select(topic => ListenToTopicAsync(topic, stoppingToken));
        await Task.WhenAll(tasks);
    }

    internal async Task ListenToTopicAsync(string topic, CancellationToken stoppingToken)
    {
        var initialDelay = _options.ReconnectInitialDelayMilliseconds > 0
            ? TimeSpan.FromMilliseconds(_options.ReconnectInitialDelayMilliseconds)
            : TimeSpan.FromSeconds(1);
        var reconnect = new SseReconnectPolicy(initialDelay);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var since = _connections.GetSinceCursor(topic);
                var url = SseReconnectPolicy.BuildSubscribeUrl(_options.NtfyUrl, topic, since);
                _logger.LogInformation("Connecting to ntfy topic: {Topic} (since={Since})", topic, since ?? "live");

                var client = _httpClientFactory.CreateClient("NtfyClient");
                client.Timeout = Timeout.InfiniteTimeSpan;

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(_options.NtfyToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.NtfyToken);
                }

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stoppingToken);
                response.EnsureSuccessStatusCode();

                _connections.MarkConnected(topic);
                if (_options.SendRecoveryAlerts && _connections.ShouldSendRecovery(topic))
                {
                    await SendRecoveryAlertAsync(topic, stoppingToken);
                    _connections.MarkRecoverySent(topic);
                }

                reconnect.Reset();

                using var stream = await response.Content.ReadAsStreamAsync(stoppingToken);
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream && !stoppingToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(stoppingToken);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var message = JsonSerializer.Deserialize<NtfyMessage>(line);
                        if (message == null)
                        {
                            continue;
                        }

                        if (message.Event == "message")
                        {
                            var handled = await HandleMessageAsync(topic, message, stoppingToken);
                            if (!handled)
                            {
                                _logger.LogWarning(
                                    "Transient failure handling message {MessageId} on topic {Topic}; stopping stream to reconnect and retry in order.",
                                    message.Id, topic);
                                break;
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, "Failed to parse message from ntfy: {Line}", line);
                    }
                }

                // Clean EOF: apply same backoff/recovery path as hard failures to avoid reconnect storms.
                _connections.MarkDisconnected(topic);
                _connections.MarkOutageAlerted(topic);
                _metrics.Reconnect(topic);
                var eofDelay = reconnect.NextDelay();
                _logger.LogWarning(
                    "SSE stream ended for topic {Topic}. Retrying in {DelaySeconds:F1}s (attempt {Attempt})...",
                    topic, eofDelay.TotalSeconds, reconnect.Attempt);
                await Task.Delay(eofDelay, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _connections.MarkDisconnected(topic);
                _connections.MarkOutageAlerted(topic);
                _metrics.Reconnect(topic);
                var delay = reconnect.NextDelay();
                _logger.LogError(ex, "Error listening to topic {Topic}. Retrying in {DelaySeconds:F1}s (attempt {Attempt})...",
                    topic, delay.TotalSeconds, reconnect.Attempt);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    private async Task SendRecoveryAlertAsync(string topic, CancellationToken stoppingToken)
    {
        try
        {
            if (!_options.TopicTokens.TryGetValue(topic, out var token) || string.IsNullOrEmpty(token))
            {
                token = _options.PushoverDefaultToken;
            }

            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(_options.PushoverUserKey))
            {
                return;
            }

            using var content = new MultipartFormDataContent
            {
                { new StringContent(token), "token" },
                { new StringContent(_options.PushoverUserKey), "user" },
                { new StringContent($"ntfy recovered: {topic}"), "title" },
                { new StringContent($"SSE connection to topic '{topic}' restored after outage."), "message" },
                { new StringContent("0"), "priority" },
                { new StringContent("magic"), "sound" }
            };

            var client = _httpClientFactory.CreateClient("PushoverClient");
            var response = await client.PostAsync(_options.PushoverUrl, content, stoppingToken);
            if (response.IsSuccessStatusCode)
            {
                _metrics.RecoveryAlert(topic);
                _logger.LogInformation("Sent recovery alert for topic {Topic}", topic);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send recovery alert for topic {Topic}", topic);
        }
    }

    /// <summary>
    /// Forward a message, then advance the SSE cursor only when the message was handled
    /// (successful forward or intentional drop). Failed Pushover posts leave the cursor
    /// unchanged so reconnect can retry.
    /// </summary>
    /// <returns>
    /// True if the message was handled (successful forward, intentional drop, or permanent failure)
    /// and stream processing should continue; false if a transient failure occurred and the stream
    /// should be stopped to reconnect and retry.
    /// </returns>
    internal async Task<bool> HandleMessageAsync(string topic, NtfyMessage message, CancellationToken stoppingToken)
    {
        var result = await ForwardToPushoverAsync(topic, message, stoppingToken);
        switch (result)
        {
            case ForwardResult.Success:
                _connections.NoteMessage(topic, message.Id, message.Time);
                return true;

            case ForwardResult.PermanentFailure:
                // Permanently invalid messages must not block the stream forever.
                // Do not mark as unresolved transient failure, but do not advance cursor on its own either.
                return true;

            case ForwardResult.TransientFailure:
            default:
                _connections.NoteMessageFailed(topic, message.Id);
                return false;
        }
    }

    /// <returns>
    /// ForwardResult indicating Success, TransientFailure, or PermanentFailure.
    /// </returns>
    internal async Task<ForwardResult> ForwardToPushoverAsync(string topic, NtfyMessage message, CancellationToken stoppingToken)
    {
        try
        {
            _metrics.Received(topic);

            if (string.IsNullOrWhiteSpace(message.Id))
            {
                _logger.LogError("Message has no Id on topic {Topic}", topic);
                _metrics.ForwardFailed(topic);
                return ForwardResult.PermanentFailure;
            }

            var title = !string.IsNullOrEmpty(message.Title) ? message.Title : $"ntfy: {topic}";
            var msgBody = !string.IsNullOrEmpty(message.Message) ? message.Message : "No message body";
            var tags = message.Tags ?? Array.Empty<string>();

            if (!PriorityMapper.MeetsMinimum(message.Priority, _options.MinimumPriority))
            {
                _metrics.DroppedPriority(topic);
                _logger.LogDebug(
                    "Message dropped due to low priority ({Priority} < {MinimumPriority}).",
                    PriorityMapper.ResolveNtfyPriority(message.Priority),
                    _options.MinimumPriority);
                return ForwardResult.Success;
            }

            var fingerprint = MessageFingerprint.Build(topic, title, msgBody, tags, message);
            var window = TimeSpan.FromSeconds(_options.DeduplicationWindowSeconds);
            if (_dedupeStore.Contains(fingerprint, DateTimeOffset.UtcNow, window))
            {
                _metrics.DroppedDedupe(topic);
                _logger.LogInformation("Duplicate ntfy message suppressed for topic {Topic}: {Title}", topic, title);
                return ForwardResult.Success;
            }

            var priority = PriorityMapper.ToPushover(message.Priority);

            if (!_options.TopicTokens.TryGetValue(topic, out var token) || string.IsNullOrEmpty(token))
            {
                token = _options.PushoverDefaultToken;
            }

            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(_options.PushoverUserKey))
            {
                _logger.LogError("No Pushover token or user key available for topic {Topic}", topic);
                _metrics.ForwardFailed(topic);
                return ForwardResult.PermanentFailure;
            }

            string sound = "pushover";
            foreach (var tag in tags)
            {
                if (_options.SoundMap.TryGetValue(tag, out var mappedSound))
                {
                    sound = mappedSound;
                    break;
                }
            }

            string? device = tags.FirstOrDefault(t => t.StartsWith("device:", StringComparison.Ordinal))?.Substring(7);

            var formattedBody = MessageFormatting.ApplyFormatting(msgBody, message);
            var useHtml = MessageFormatting.ShouldUseHtml(message, formattedBody);

            _logger.LogInformation("Forwarding {Topic} [{Sound}]: {Title}", topic, sound, title);

            var pushoverClient = _httpClientFactory.CreateClient("PushoverClient");
            var maxAttempts = Math.Max(1, _options.MaxRetries);
            int attempts = 0;

            while (attempts < maxAttempts)
            {
                attempts++;
                try
                {
                    using var content = new MultipartFormDataContent();
                    content.Add(new StringContent(token), "token");
                    content.Add(new StringContent(_options.PushoverUserKey), "user");
                    content.Add(new StringContent(title), "title");
                    content.Add(new StringContent(formattedBody), "message");
                    content.Add(new StringContent(priority.ToString()), "priority");
                    content.Add(new StringContent(sound), "sound");

                    if (useHtml)
                    {
                        content.Add(new StringContent("1"), "html");
                    }

                    if (!string.IsNullOrEmpty(device))
                    {
                        content.Add(new StringContent(device), "device");
                    }

                    if (!string.IsNullOrEmpty(message.Click))
                    {
                        content.Add(new StringContent(message.Click), "url");
                    }

                    if (priority == 2)
                    {
                        content.Add(new StringContent("60"), "retry");
                        content.Add(new StringContent("3600"), "expire");
                    }

                    await AttachImageAsync(content, topic, message, tags, stoppingToken);

                    var response = await pushoverClient.PostAsync(_options.PushoverUrl, content, stoppingToken);

                    if (response.IsSuccessStatusCode)
                    {
                        // Only suppress duplicates after a successful forward.
                        _dedupeStore.TryRecord(fingerprint, DateTimeOffset.UtcNow, window, _options.DeduplicationMaxEntries);
                        _metrics.Forwarded(topic);
                        return ForwardResult.Success;
                    }

                    var responseBody = await response.Content.ReadAsStringAsync(stoppingToken);

                    // Check if permanent error: HTTP 4xx except 429 Too Many Requests
                    if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500 && response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                    {
                        _logger.LogError("Pushover API Permanent Error {StatusCode}: {ResponseBody}", response.StatusCode, responseBody);
                        _metrics.ForwardFailed(topic);
                        return ForwardResult.PermanentFailure;
                    }

                    _logger.LogWarning("Pushover API Error {StatusCode} (attempt {Attempt}/{MaxAttempts}): {ResponseBody}",
                        response.StatusCode, attempts, maxAttempts, responseBody);

                    if (attempts < maxAttempts && _options.RetryDelayMilliseconds > 0)
                    {
                        await Task.Delay(_options.RetryDelayMilliseconds, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Exception forwarding message to Pushover for topic {Topic} (attempt {Attempt}/{MaxAttempts})",
                        topic, attempts, maxAttempts);

                    if (attempts >= maxAttempts)
                    {
                        _metrics.ForwardFailed(topic);
                        return ForwardResult.TransientFailure;
                    }

                    if (_options.RetryDelayMilliseconds > 0)
                    {
                        await Task.Delay(_options.RetryDelayMilliseconds, stoppingToken);
                    }
                }
            }

            _metrics.ForwardFailed(topic);
            return ForwardResult.TransientFailure;
        }
        catch (Exception ex)
        {
            _metrics.ForwardFailed(topic);
            _logger.LogError(ex, "Error forwarding message to Pushover for topic {Topic}", topic);
            return ForwardResult.TransientFailure;
        }
    }

    internal async Task AttachImageAsync(
        MultipartFormDataContent content,
        string topic,
        NtfyMessage message,
        string[] tags,
        CancellationToken stoppingToken)
    {
        string? attUrl = null;
        bool isLogo = false;

        if (message.Attachment != null && !string.IsNullOrEmpty(message.Attachment.Url))
        {
            attUrl = message.Attachment.Url;
            if (attUrl.StartsWith('/'))
            {
                attUrl = $"{_options.NtfyUrl.TrimEnd('/')}{attUrl}";
            }
            else if (!Uri.TryCreate(attUrl, UriKind.Absolute, out _))
            {
                attUrl = $"{_options.NtfyUrl.TrimEnd('/')}/{attUrl}";
            }
        }
        else
        {
            foreach (var tag in tags)
            {
                if (_options.LogoMap.TryGetValue(tag, out var mappedLogo))
                {
                    attUrl = mappedLogo;
                    isLogo = true;
                    break;
                }
            }

            if (string.IsNullOrEmpty(attUrl) && topic.Contains("media", StringComparison.OrdinalIgnoreCase))
            {
                _options.LogoMap.TryGetValue("mag", out attUrl);
                isLogo = true;
            }
        }

        if (string.IsNullOrEmpty(attUrl))
        {
            return;
        }

        byte[]? fileBytes = null;
        string fileName = "image.png";
        string mediaType = "image/png";

        if (isLogo && _iconCache.TryGetValue(attUrl, out var cachedBytes))
        {
            fileBytes = cachedBytes;
        }
        else
        {
            try
            {
                var client = _httpClientFactory.CreateClient("AttachmentClient");
                client.Timeout = TimeSpan.FromSeconds(10);
                using var request = new HttpRequestMessage(HttpMethod.Get, attUrl);
                if (!isLogo && !string.IsNullOrEmpty(_options.NtfyToken) && IsSameOrigin(attUrl, _options.NtfyUrl))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.NtfyToken);
                }

                using var attResponse = await client.SendAsync(request, stoppingToken);
                if (attResponse.IsSuccessStatusCode)
                {
                    fileBytes = await attResponse.Content.ReadAsByteArrayAsync(stoppingToken);
                    mediaType = attResponse.Content.Headers.ContentType?.MediaType ?? (isLogo ? "image/png" : "image/jpeg");
                    fileName = isLogo ? "logo.png" : "attachment" + (mediaType.Contains("png", StringComparison.Ordinal) ? ".png" : ".jpg");

                    if (isLogo)
                    {
                        _iconCache[attUrl] = fileBytes;
                    }
                }
                else
                {
                    _logger.LogWarning("Failed to download attachment/logo from {Url}: HTTP {StatusCode}", attUrl, attResponse.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download attachment/logo from {Url}", attUrl);
            }
        }

        if (fileBytes != null)
        {
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            content.Add(fileContent, "attachment", fileName);
        }
    }

    internal static bool IsSameOrigin(string? targetUrl, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl) || string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri) ||
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        return string.Equals(targetUri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(targetUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
               targetUri.Port == baseUri.Port;
    }
}
