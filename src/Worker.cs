using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NtfyPushoverForwarder.Models;

namespace NtfyPushoverForwarder;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ForwarderOptions _options;
    private readonly Dictionary<string, byte[]> _iconCache = new();

    public Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory, IOptions<ForwarderOptions> options)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
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

    private async Task ListenToTopicAsync(string topic, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Connecting to ntfy topic: {Topic}", topic);
                var client = _httpClientFactory.CreateClient("NtfyClient");
                client.Timeout = Timeout.InfiniteTimeSpan;

                if (!string.IsNullOrEmpty(_options.NtfyToken))
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.NtfyToken);
                }

                var url = $"{_options.NtfyUrl.TrimEnd('/')}/{topic}/json";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stoppingToken);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(stoppingToken);
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream && !stoppingToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(stoppingToken);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var message = JsonSerializer.Deserialize<NtfyMessage>(line);
                        if (message != null && message.Event == "message")
                        {
                            await ForwardToPushoverAsync(topic, message, stoppingToken);
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, "Failed to parse message from ntfy: {Line}", line);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error listening to topic {Topic}. Retrying in 5 seconds...", topic);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ForwardToPushoverAsync(string topic, NtfyMessage message, CancellationToken stoppingToken)
    {
        try
        {
            var title = !string.IsNullOrEmpty(message.Title) ? message.Title : $"ntfy: {topic}";
            var msgBody = !string.IsNullOrEmpty(message.Message) ? message.Message : "No message body";
            var tags = message.Tags ?? Array.Empty<string>();

            // Map Priority (ntfy 1-5 to pushover -2 to 2)
            int priority = 0;
            if (message.Priority >= 5) priority = 2;
            else if (message.Priority == 4) priority = 1;
            else if (message.Priority <= 2) priority = -1;

            // Select Token
            if (!_options.TopicTokens.TryGetValue(topic, out var token) || string.IsNullOrEmpty(token))
            {
                token = _options.PushoverDefaultToken;
            }

            if (string.IsNullOrEmpty(token))
            {
                _logger.LogError("No Pushover token available for topic {Topic}", topic);
                return;
            }

            // Sound Mapping
            string sound = "pushover";
            foreach (var tag in tags)
            {
                if (_options.SoundMap.TryGetValue(tag, out var mappedSound))
                {
                    sound = mappedSound;
                    break;
                }
            }

            // Device Targeting
            string? device = tags.FirstOrDefault(t => t.StartsWith("device:"))?.Substring(7);

            _logger.LogInformation("Forwarding {Topic} [{Sound}]: {Title}", topic, sound, title);

            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(token), "token");
            content.Add(new StringContent(_options.PushoverUserKey), "user");
            content.Add(new StringContent(title), "title");
            content.Add(new StringContent(msgBody), "message");
            content.Add(new StringContent(priority.ToString()), "priority");
            content.Add(new StringContent(sound), "sound");

            if (!string.IsNullOrEmpty(device))
                content.Add(new StringContent(device), "device");

            if (!string.IsNullOrEmpty(message.Click))
                content.Add(new StringContent(message.Click), "url");

            if (priority == 2)
            {
                content.Add(new StringContent("60"), "retry");
                content.Add(new StringContent("3600"), "expire");
            }

            // Attachment / Logo Logic
            string? attUrl = null;
            bool isLogo = false;

            if (message.Attachment != null && !string.IsNullOrEmpty(message.Attachment.Url))
            {
                attUrl = message.Attachment.Url;
                if (attUrl.StartsWith("/"))
                {
                    attUrl = $"{_options.NtfyUrl.TrimEnd('/')}{attUrl}";
                }
            }
            else
            {
                // Fallback to logo mapping
                foreach (var tag in tags)
                {
                    if (_options.LogoMap.TryGetValue(tag, out var mappedLogo))
                    {
                        attUrl = mappedLogo;
                        isLogo = true;
                        break;
                    }
                }
                
                if (string.IsNullOrEmpty(attUrl) && topic.Contains("media"))
                {
                    _options.LogoMap.TryGetValue("mag", out attUrl);
                    isLogo = true;
                }
            }

            if (!string.IsNullOrEmpty(attUrl))
            {
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
                        var attResponse = await client.GetAsync(attUrl, stoppingToken);
                        if (attResponse.IsSuccessStatusCode)
                        {
                            fileBytes = await attResponse.Content.ReadAsByteArrayAsync(stoppingToken);
                            mediaType = attResponse.Content.Headers.ContentType?.MediaType ?? (isLogo ? "image/png" : "image/jpeg");
                            fileName = isLogo ? "logo.png" : "attachment" + (mediaType.Contains("png") ? ".png" : ".jpg");

                            if (isLogo)
                            {
                                _iconCache[attUrl] = fileBytes;
                            }
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

            var pushoverClient = _httpClientFactory.CreateClient("PushoverClient");
            var response = await pushoverClient.PostAsync(_options.PushoverUrl, content, stoppingToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(stoppingToken);
                _logger.LogError("Pushover API Error {StatusCode}: {ResponseBody}", response.StatusCode, responseBody);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error forwarding message to Pushover for topic {Topic}", topic);
        }
    }
}
