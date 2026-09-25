using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NtfyPushoverForwarder;
using NtfyPushoverForwarder.Models;

namespace NtfyPushoverForwarder.Tests;

public class AttachmentDownloadTests
{
    private class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public HttpRequestMessage? LastRequest { get; private set; }

        public TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_handler(request));
        }
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public TestHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options.Name, options.Version);
        public void Dispose() { }
    }

    private static Worker CreateWorker(ForwarderOptions options, HttpMessageHandler handler)
    {
        return new Worker(
            NullLogger<Worker>.Instance,
            new TestHttpClientFactory(handler),
            Options.Create(options),
            new ForwarderMetrics(new TestMeterFactory()),
            new TopicConnectionTracker(),
            new InMemoryDedupeStore());
    }

    [Fact]
    public async Task AttachImageAsync_WithNtfyToken_SendsAuthorizationHeader()
    {
        var testToken = "REDACTED_TEST_VALUE";
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            NtfyToken = testToken
        };

        HttpRequestMessage? interceptedRequest = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            interceptedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) // PNG header
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });

        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment
            {
                Url = "http://ntfy.example.test/file/private-photo.png"
            }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.NotNull(interceptedRequest);
        Assert.NotNull(interceptedRequest.Headers.Authorization);
        Assert.Equal("Bearer", interceptedRequest.Headers.Authorization.Scheme);
        Assert.Equal(testToken, interceptedRequest.Headers.Authorization.Parameter);
        Assert.Contains(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_WithRelativeUrl_PrependsNtfyUrlAndSendsToken()
    {
        var testToken = "REDACTED_TEST_VALUE";
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            NtfyToken = testToken
        };

        HttpRequestMessage? interceptedRequest = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            interceptedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF }) // JPEG header
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return response;
        });

        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment
            {
                Url = "/file/relative-photo.jpg"
            }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.NotNull(interceptedRequest);
        Assert.Equal("http://ntfy.example.test/file/relative-photo.jpg", interceptedRequest.RequestUri?.ToString());
        Assert.NotNull(interceptedRequest.Headers.Authorization);
        Assert.Equal("Bearer", interceptedRequest.Headers.Authorization.Scheme);
        Assert.Equal(testToken, interceptedRequest.Headers.Authorization.Parameter);
        Assert.Contains(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_WithoutNtfyToken_DoesNotSendAuthorizationHeader()
    {
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            NtfyToken = string.Empty
        };

        HttpRequestMessage? interceptedRequest = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            interceptedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0x89, 0x50, 0x4E, 0x47 })
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });

        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment
            {
                Url = "http://ntfy.example.test/file/public-photo.png"
            }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.NotNull(interceptedRequest);
        Assert.Null(interceptedRequest.Headers.Authorization);
        Assert.Contains(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_ForLogo_DoesNotSendAuthorizationHeader()
    {
        var testToken = "REDACTED_TEST_VALUE";
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            NtfyToken = testToken,
            LogoMap = new Dictionary<string, string>
            {
                ["camera"] = "https://external.example.test/camera.png"
            }
        };

        HttpRequestMessage? interceptedRequest = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            interceptedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0x89, 0x50, 0x4E, 0x47 })
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });

        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = null
        };

        await worker.AttachImageAsync(content, "test-topic", message, new[] { "camera" }, CancellationToken.None);

        Assert.NotNull(interceptedRequest);
        Assert.Equal("https://external.example.test/camera.png", interceptedRequest.RequestUri?.ToString());
        Assert.Null(interceptedRequest.Headers.Authorization);
        Assert.Contains(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_WhenDownloadFails_DoesNotAddAttachment()
    {
        var testToken = "REDACTED_TEST_VALUE";
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            NtfyToken = testToken
        };

        var handler = new TestHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment
            {
                Url = "http://ntfy.example.test/file/private-photo.png"
            }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.DoesNotContain(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }
}
