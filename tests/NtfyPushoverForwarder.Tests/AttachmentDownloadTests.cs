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

    private class ThrowOnReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Stream should not be read when rejected early.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new InvalidOperationException("Stream should not be read when rejected early.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Stream should not be read when rejected early.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private class NonSeekableChunkedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private int _position;

        public NonSeekableChunkedStream(byte[] data, int chunkSize = 64)
        {
            _data = data;
            _chunkSize = chunkSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length) return 0;
            int toRead = Math.Min(Math.Min(count, _chunkSize), _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _data.Length) return ValueTask.FromResult(0);
            int toRead = Math.Min(Math.Min(buffer.Length, _chunkSize), _data.Length - _position);
            _data.AsSpan(_position, toRead).CopyTo(buffer.Span);
            _position += toRead;
            return ValueTask.FromResult(toRead);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private class TrackedHttpResponseMessage : HttpResponseMessage
    {
        public bool IsDisposed { get; private set; }

        public TrackedHttpResponseMessage(HttpStatusCode statusCode) : base(statusCode) { }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                IsDisposed = true;
            }
        }
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

    [Fact]
    public async Task AttachImageAsync_WithForeignHostAbsoluteUrl_DoesNotSendAuthorizationHeader()
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
                Url = "http://foreign.example.test/file/foreign-photo.png"
            }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.NotNull(interceptedRequest);
        Assert.Equal("http://foreign.example.test/file/foreign-photo.png", interceptedRequest.RequestUri?.ToString());
        Assert.Null(interceptedRequest.Headers.Authorization);
        Assert.Contains(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_WithLookalikeHost_DoesNotSendAuthorizationHeader()
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
                Url = "http://ntfy.example.test.evil.example/file/evil-photo.png"
            }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.NotNull(interceptedRequest);
        Assert.Equal("http://ntfy.example.test.evil.example/file/evil-photo.png", interceptedRequest.RequestUri?.ToString());
        Assert.Null(interceptedRequest.Headers.Authorization);
        Assert.Contains(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_WithSameOriginAbsoluteUrl_SendsAuthorizationHeader()
    {
        var testToken = "REDACTED_TEST_VALUE";
        var options = new ForwarderOptions
        {
            NtfyUrl = "https://ntfy.example.test:8443",
            NtfyToken = testToken
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
                Url = "https://ntfy.example.test:8443/file/secure-photo.png"
            }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.NotNull(interceptedRequest);
        Assert.Equal("https://ntfy.example.test:8443/file/secure-photo.png", interceptedRequest.RequestUri?.ToString());
        Assert.NotNull(interceptedRequest.Headers.Authorization);
        Assert.Equal("Bearer", interceptedRequest.Headers.Authorization.Scheme);
        Assert.Equal(testToken, interceptedRequest.Headers.Authorization.Parameter);
        Assert.Contains(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_ForSameOriginLogo_DoesNotSendAuthorizationHeader()
    {
        var testToken = "REDACTED_TEST_VALUE";
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            NtfyToken = testToken,
            LogoMap = new Dictionary<string, string>
            {
                ["camera"] = "http://ntfy.example.test/camera.png"
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
        Assert.Equal("http://ntfy.example.test/camera.png", interceptedRequest.RequestUri?.ToString());
        Assert.Null(interceptedRequest.Headers.Authorization);
        Assert.Contains(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Theory]
    [InlineData("http://ntfy.example.test/file.png", "http://ntfy.example.test", true)]
    [InlineData("http://ntfy.example.test/file.png", "http://ntfy.example.test:80", true)]
    [InlineData("http://ntfy.example.test:80/file.png", "http://ntfy.example.test", true)]
    [InlineData("https://ntfy.example.test/file.png", "https://ntfy.example.test", true)]
    [InlineData("https://ntfy.example.test:443/file.png", "https://ntfy.example.test", true)]
    [InlineData("http://ntfy.example.test:8080/file.png", "http://ntfy.example.test:8080", true)]
    [InlineData("http://foreign.example.test/file.png", "http://ntfy.example.test", false)]
    [InlineData("http://ntfy.example.test.evil.example/file.png", "http://ntfy.example.test", false)]
    [InlineData("http://evil-ntfy.example.test/file.png", "http://ntfy.example.test", false)]
    [InlineData("http://ntfy.example.test:8080/file.png", "http://ntfy.example.test", false)]
    [InlineData("https://ntfy.example.test/file.png", "http://ntfy.example.test", false)]
    [InlineData("http://ntfy.example.test/file.png", "https://ntfy.example.test", false)]
    [InlineData("/file.png", "http://ntfy.example.test", false)]
    [InlineData(null, "http://ntfy.example.test", false)]
    [InlineData("http://ntfy.example.test/file.png", null, false)]
    [InlineData("not-a-valid-url", "http://ntfy.example.test", false)]
    public void IsSameOrigin_ValidatesOriginCorrectly(string? targetUrl, string? baseUrl, bool expected)
    {
        var result = Worker.IsSameOrigin(targetUrl, baseUrl);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task AttachImageAsync_NormalPngAttachment_AddsAttachmentWithPngExtension()
    {
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test"
        };

        var handler = new TestHttpMessageHandler(req =>
        {
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
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/file.png" }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        var attachmentPart = content.FirstOrDefault(c => c.Headers.ContentDisposition?.Name == "attachment");
        Assert.NotNull(attachmentPart);
        Assert.Equal("attachment.png", attachmentPart.Headers.ContentDisposition?.FileName);
        Assert.Equal("image/png", attachmentPart.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AttachImageAsync_NormalJpegAttachment_AddsAttachmentWithJpgExtension()
    {
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test"
        };

        var handler = new TestHttpMessageHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return response;
        });

        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/file.jpg" }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        var attachmentPart = content.FirstOrDefault(c => c.Headers.ContentDisposition?.Name == "attachment");
        Assert.NotNull(attachmentPart);
        Assert.Equal("attachment.jpg", attachmentPart.Headers.ContentDisposition?.FileName);
        Assert.Equal("image/jpeg", attachmentPart.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AttachImageAsync_OversizedByContentLength_RejectsEarlyWithoutReadingStream()
    {
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test"
        };

        var handler = new TestHttpMessageHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowOnReadStream())
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            response.Content.Headers.ContentLength = 3_000_000;
            return response;
        });

        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/large.png" }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.DoesNotContain(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_OversizedByContentLength_RespectsCustomMaxAttachmentBytes()
    {
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            MaxAttachmentBytes = 500
        };

        var handler = new TestHttpMessageHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowOnReadStream())
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            response.Content.Headers.ContentLength = 501;
            return response;
        });

        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/custom-limit.png" }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.DoesNotContain(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_OversizedByMisleadingContentLength_AbortsWhileStreaming()
    {
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            MaxAttachmentBytes = 100
        };

        var streamData = new byte[256];
        Array.Fill(streamData, (byte)0x5A);

        var handler = new TestHttpMessageHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableChunkedStream(streamData, chunkSize: 32))
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            response.Content.Headers.ContentLength = 50;
            return response;
        });

        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/misleading.png" }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.DoesNotContain(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_OversizedByMissingContentLength_AbortsWhileStreaming()
    {
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            MaxAttachmentBytes = 100
        };

        var streamData = new byte[256];
        Array.Fill(streamData, (byte)0x5A);

        var handler = new TestHttpMessageHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableChunkedStream(streamData, chunkSize: 32))
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });

        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/missing-cl.png" }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.DoesNotContain(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_WhenServerReturns500_DoesNotAddAttachment()
    {
        var options = new ForwarderOptions { NtfyUrl = "http://ntfy.example.test" };
        var handler = new TestHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/error500.png" }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.DoesNotContain(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_WhenServerReturns404_DoesNotAddAttachment()
    {
        var options = new ForwarderOptions { NtfyUrl = "http://ntfy.example.test" };
        var handler = new TestHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound));
        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/notfound.png" }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.DoesNotContain(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_WhenDownloadThrowsException_DoesNotAddAttachment()
    {
        var options = new ForwarderOptions { NtfyUrl = "http://ntfy.example.test" };
        var handler = new TestHttpMessageHandler(req => throw new HttpRequestException("Network failure"));
        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/throws.png" }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.DoesNotContain(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    [InlineData("text/html")]
    public async Task AttachImageAsync_UnsupportedContentType_DoesNotAddAttachment(string unsupportedType)
    {
        var options = new ForwarderOptions { NtfyUrl = "http://ntfy.example.test" };
        var handler = new TestHttpMessageHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(unsupportedType);
            return response;
        });

        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/file.pdf" }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.DoesNotContain(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_MissingContentType_DoesNotAddAttachment()
    {
        var options = new ForwarderOptions { NtfyUrl = "http://ntfy.example.test" };
        var handler = new TestHttpMessageHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
            };
            response.Content.Headers.ContentType = null;
            return response;
        });

        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/file.unknown" }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.DoesNotContain(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public async Task AttachImageAsync_EmptyContent_DoesNotAddAttachment()
    {
        var options = new ForwarderOptions { NtfyUrl = "http://ntfy.example.test" };
        var handler = new TestHttpMessageHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });

        var worker = CreateWorker(options, handler);
        using var content = new MultipartFormDataContent();
        var message = new NtfyMessage
        {
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/empty.png" }
        };

        await worker.AttachImageAsync(content, "test-topic", message, Array.Empty<string>(), CancellationToken.None);

        Assert.DoesNotContain(content, c => c.Headers.ContentDisposition?.Name == "attachment");
    }

    [Fact]
    public void ForwarderOptions_DefaultMaxAttachmentBytes_IsPushoverLimit()
    {
        var options = new ForwarderOptions();
        Assert.Equal(2621440, options.MaxAttachmentBytes);
    }

    [Fact]
    public async Task ForwardToPushoverAsync_NormalAttachment_SendsAlertWithAttachment()
    {
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            PushoverUserKey = "test-user-key",
            PushoverDefaultToken = "test-token"
        };

        bool? pushoverHasAttachment = null;
        bool? pushoverHasMessage = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            if (req.RequestUri?.ToString().Contains("pushover.net") == true)
            {
                if (req.Content is MultipartFormDataContent form)
                {
                    pushoverHasAttachment = form.Any(c => c.Headers.ContentDisposition?.Name == "attachment");
                    pushoverHasMessage = form.Any(c => c.Headers.ContentDisposition?.Name == "message");
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":1}")
                };
            }

            var attResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0x89, 0x50, 0x4E, 0x47 })
            };
            attResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return attResponse;
        });

        var worker = CreateWorker(options, handler);
        var message = new NtfyMessage
        {
            Id = "msg-normal",
            Time = 1234567890,
            Title = "Alert Title",
            Message = "Alert Message",
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/photo.png" }
        };

        var result = await worker.ForwardToPushoverAsync("test-topic", message, CancellationToken.None);

        Assert.Equal(ForwardResult.Success, result);
        Assert.True(pushoverHasMessage);
        Assert.True(pushoverHasAttachment);
    }

    [Fact]
    public async Task ForwardToPushoverAsync_OversizedByContentLength_SkipsAttachmentAndSendsTextAlert()
    {
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            PushoverUserKey = "test-user-key",
            PushoverDefaultToken = "test-token"
        };

        bool? pushoverHasAttachment = null;
        bool? pushoverHasMessage = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            if (req.RequestUri?.ToString().Contains("pushover.net") == true)
            {
                if (req.Content is MultipartFormDataContent form)
                {
                    pushoverHasAttachment = form.Any(c => c.Headers.ContentDisposition?.Name == "attachment");
                    pushoverHasMessage = form.Any(c => c.Headers.ContentDisposition?.Name == "message");
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":1}")
                };
            }

            var attResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowOnReadStream())
            };
            attResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            attResponse.Content.Headers.ContentLength = 5_000_000;
            return attResponse;
        });

        var worker = CreateWorker(options, handler);
        var message = new NtfyMessage
        {
            Id = "msg-oversized-cl",
            Time = 1234567890,
            Title = "Alert Title",
            Message = "Alert Message",
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/large.png" }
        };

        var result = await worker.ForwardToPushoverAsync("test-topic", message, CancellationToken.None);

        Assert.Equal(ForwardResult.Success, result);
        Assert.True(pushoverHasMessage);
        Assert.False(pushoverHasAttachment);
    }

    [Fact]
    public async Task ForwardToPushoverAsync_OversizedByMisleadingContentLength_SkipsAttachmentAndSendsTextAlert()
    {
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            PushoverUserKey = "test-user-key",
            PushoverDefaultToken = "test-token",
            MaxAttachmentBytes = 100
        };

        var streamData = new byte[256];
        Array.Fill(streamData, (byte)0x5A);

        bool? pushoverHasAttachment = null;
        bool? pushoverHasMessage = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            if (req.RequestUri?.ToString().Contains("pushover.net") == true)
            {
                if (req.Content is MultipartFormDataContent form)
                {
                    pushoverHasAttachment = form.Any(c => c.Headers.ContentDisposition?.Name == "attachment");
                    pushoverHasMessage = form.Any(c => c.Headers.ContentDisposition?.Name == "message");
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":1}")
                };
            }

            var attResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableChunkedStream(streamData, chunkSize: 32))
            };
            attResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            attResponse.Content.Headers.ContentLength = 50; // Misleading
            return attResponse;
        });

        var worker = CreateWorker(options, handler);
        var message = new NtfyMessage
        {
            Id = "msg-misleading-cl",
            Time = 1234567890,
            Title = "Alert Title",
            Message = "Alert Message",
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/misleading.png" }
        };

        var result = await worker.ForwardToPushoverAsync("test-topic", message, CancellationToken.None);

        Assert.Equal(ForwardResult.Success, result);
        Assert.True(pushoverHasMessage);
        Assert.False(pushoverHasAttachment);
    }

    [Fact]
    public async Task ForwardToPushoverAsync_OversizedByMissingContentLength_SkipsAttachmentAndSendsTextAlert()
    {
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            PushoverUserKey = "test-user-key",
            PushoverDefaultToken = "test-token",
            MaxAttachmentBytes = 100
        };

        var streamData = new byte[256];
        Array.Fill(streamData, (byte)0x5A);

        bool? pushoverHasAttachment = null;
        bool? pushoverHasMessage = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            if (req.RequestUri?.ToString().Contains("pushover.net") == true)
            {
                if (req.Content is MultipartFormDataContent form)
                {
                    pushoverHasAttachment = form.Any(c => c.Headers.ContentDisposition?.Name == "attachment");
                    pushoverHasMessage = form.Any(c => c.Headers.ContentDisposition?.Name == "message");
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":1}")
                };
            }

            var attResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableChunkedStream(streamData, chunkSize: 32))
            };
            attResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return attResponse;
        });

        var worker = CreateWorker(options, handler);
        var message = new NtfyMessage
        {
            Id = "msg-missing-cl",
            Time = 1234567890,
            Title = "Alert Title",
            Message = "Alert Message",
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/missing.png" }
        };

        var result = await worker.ForwardToPushoverAsync("test-topic", message, CancellationToken.None);

        Assert.Equal(ForwardResult.Success, result);
        Assert.True(pushoverHasMessage);
        Assert.False(pushoverHasAttachment);
    }

    [Fact]
    public async Task ForwardToPushoverAsync_WhenAttachmentFails_SkipsAttachmentAndSendsTextAlert()
    {
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            PushoverUserKey = "test-user-key",
            PushoverDefaultToken = "test-token"
        };

        bool? pushoverHasAttachment = null;
        bool? pushoverHasMessage = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            if (req.RequestUri?.ToString().Contains("pushover.net") == true)
            {
                if (req.Content is MultipartFormDataContent form)
                {
                    pushoverHasAttachment = form.Any(c => c.Headers.ContentDisposition?.Name == "attachment");
                    pushoverHasMessage = form.Any(c => c.Headers.ContentDisposition?.Name == "message");
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":1}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var worker = CreateWorker(options, handler);
        var message = new NtfyMessage
        {
            Id = "msg-failed-att",
            Time = 1234567890,
            Title = "Alert Title",
            Message = "Alert Message",
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/fail.png" }
        };

        var result = await worker.ForwardToPushoverAsync("test-topic", message, CancellationToken.None);

        Assert.Equal(ForwardResult.Success, result);
        Assert.True(pushoverHasMessage);
        Assert.False(pushoverHasAttachment);
    }

    [Fact]
    public async Task ForwardToPushoverAsync_UnsupportedContentType_SkipsAttachmentAndSendsTextAlert()
    {
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            PushoverUserKey = "test-user-key",
            PushoverDefaultToken = "test-token"
        };

        bool? pushoverHasAttachment = null;
        bool? pushoverHasMessage = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            if (req.RequestUri?.ToString().Contains("pushover.net") == true)
            {
                if (req.Content is MultipartFormDataContent form)
                {
                    pushoverHasAttachment = form.Any(c => c.Headers.ContentDisposition?.Name == "attachment");
                    pushoverHasMessage = form.Any(c => c.Headers.ContentDisposition?.Name == "message");
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":1}")
                };
            }

            var attResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
            };
            attResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            return attResponse;
        });

        var worker = CreateWorker(options, handler);
        var message = new NtfyMessage
        {
            Id = "msg-unsupported",
            Time = 1234567890,
            Title = "Alert Title",
            Message = "Alert Message",
            Attachment = new NtfyAttachment { Url = "http://ntfy.example.test/document.pdf" }
        };

        var result = await worker.ForwardToPushoverAsync("test-topic", message, CancellationToken.None);

        Assert.Equal(ForwardResult.Success, result);
        Assert.True(pushoverHasMessage);
        Assert.False(pushoverHasAttachment);
    }

    [Fact]
    public async Task ForwardToPushoverAsync_DisposesNormalDeliveryHttpResponseMessage()
    {
        var options = new ForwarderOptions
        {
            NtfyUrl = "http://ntfy.example.test",
            PushoverUserKey = "test-user-key",
            PushoverDefaultToken = "test-token"
        };

        TrackedHttpResponseMessage? pushoverResponse = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            pushoverResponse = new TrackedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}")
            };
            return pushoverResponse;
        });

        var worker = CreateWorker(options, handler);
        var message = new NtfyMessage
        {
            Id = "msg-dispose-test",
            Time = 1234567890,
            Title = "Title",
            Message = "Body"
        };

        var result = await worker.ForwardToPushoverAsync("test-topic", message, CancellationToken.None);

        Assert.Equal(ForwardResult.Success, result);
        Assert.NotNull(pushoverResponse);
        Assert.True(pushoverResponse.IsDisposed, "Pushover HttpResponseMessage must be disposed with using.");
    }

    [Fact]
    public async Task SendRecoveryAlertAsync_DisposesRecoveryAlertHttpResponseMessage()
    {
        var options = new ForwarderOptions
        {
            PushoverUserKey = "test-user-key",
            PushoverDefaultToken = "test-token"
        };

        TrackedHttpResponseMessage? pushoverResponse = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            pushoverResponse = new TrackedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}")
            };
            return pushoverResponse;
        });

        var worker = CreateWorker(options, handler);
        await worker.SendRecoveryAlertAsync("test-topic", CancellationToken.None);

        Assert.NotNull(pushoverResponse);
        Assert.True(pushoverResponse.IsDisposed, "Recovery alert Pushover HttpResponseMessage must be disposed with using.");
    }
}
