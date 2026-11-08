using System.Net;
using HikingGuide.Infrastructure.Media;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Application.Media;

namespace HikingGuide.Infrastructure.Tests;

public sealed class ExternalMediaDownloadTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP+fStDAAAAAElFTkSuQmCC");

    [Fact]
    public async Task ValidImage_UsesCacheOnSecondClick()
    {
        var root = NewRoot();
        try
        {
            var count = 0;
            using var service = Create(root, _ => { count++; return Image(Png); });
            var first = await service.DownloadAsync("https://example.com/photo");
            var second = await service.DownloadAsync("https://example.com/photo");
            Assert.False(first.FromCache);
            Assert.True(second.FromCache);
            Assert.Equal(1, count);
            Assert.Equal(Png, await File.ReadAllBytesAsync(second.CachePath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("file:///C:/photo.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,AAA")]
    [InlineData("ftp://example.com/photo.png")]
    [InlineData("http://localhost/photo.png")]
    [InlineData("http://127.0.0.1/photo.png")]
    [InlineData("http://[::1]/photo.png")]
    public async Task UnsafeUrl_DoesNotIssueRequest(string url)
    {
        var root = NewRoot();
        try
        {
            var count = 0;
            using var service = Create(root, _ => { count++; return Image(Png); });
            await Assert.ThrowsAsync<ExternalMediaException>(() => service.DownloadAsync(url));
            Assert.Equal(0, count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpErrors_AreFriendly(HttpStatusCode status)
    {
        var root = NewRoot();
        try
        {
            using var service = Create(root, _ => new HttpResponseMessage(status));
            var error = await Assert.ThrowsAsync<ExternalMediaException>(() => service.DownloadAsync("https://example.com/image"));
            Assert.Contains(((int)status).ToString(), error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HtmlAndUnsafeRedirect_AreRejected()
    {
        var root = NewRoot();
        try
        {
            using var html = Create(root, _ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<html>not an image</html>") });
            await Assert.ThrowsAsync<ExternalMediaException>(() => html.DownloadAsync("https://example.com/image"));
            using var redirect = Create(root, _ => new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri("http://127.0.0.1/private") } });
            await Assert.ThrowsAsync<ExternalMediaException>(() => redirect.DownloadAsync("https://example.com/image"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SafeRedirect_RevalidatesAndDownloadsImage()
    {
        var root = NewRoot();
        try
        {
            var count = 0;
            using var service = Create(root, _ => ++count == 1
                ? new HttpResponseMessage(HttpStatusCode.Redirect)
                { Headers = { Location = new Uri("https://images.example.com/photo") } }
                : Image(Png));
            var result = await service.DownloadAsync("https://example.com/redirect");
            Assert.Equal(2, count);
            Assert.Equal("image/png", result.MimeType);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task GenericBinaryContentType_IsAcceptedOnlyWhenBytesAreAValidImage()
    {
        var root = NewRoot();
        try
        {
            using var image = Create(root, _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Png)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") }
                }
            });
            Assert.Equal("image/png", (await image.DownloadAsync("https://example.com/no-extension")).MimeType);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FakeIpProxy_RequiresIndependentPublicDnsValidation()
    {
        var root = NewRoot();
        try
        {
            using var allowed = new HttpExternalMediaDownloadService(new ApplicationDataPaths(root),
                testHandler: new FakeHandler(_ => Image(Png)),
                resolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("198.18.0.59") }),
                publicResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                trustedProxy: _ => true);
            Assert.Equal("image/png", (await allowed.DownloadAsync("https://example.com/fake-ip")).MimeType);
            using var blocked = new HttpExternalMediaDownloadService(new ApplicationDataPaths(root),
                testHandler: new FakeHandler(_ => Image(Png)),
                resolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("198.18.0.59") }),
                publicResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.1") }),
                trustedProxy: _ => true);
            await Assert.ThrowsAsync<ExternalMediaException>(() => blocked.DownloadAsync("https://example.com/private", true));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OversizeResponse_StopsWithoutCaching()
    {
        var root = NewRoot();
        try
        {
            using var service = Create(root, _ => Image(Png),
                new ExternalMediaDownloadProfile { MaximumDownloadBytes = 16 });
            var error = await Assert.ThrowsAsync<ExternalMediaException>(() => service.DownloadAsync("https://example.com/large"));
            Assert.Contains("超过", error.Message);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "ExternalMediaCache")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RedirectLoop_IsBounded()
    {
        var root = NewRoot();
        try
        {
            using var service = Create(root, _ => new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri("https://example.com/loop") } },
                new ExternalMediaDownloadProfile { MaximumRedirects = 2 });
            var error = await Assert.ThrowsAsync<ExternalMediaException>(() => service.DownloadAsync("https://example.com/loop"));
            Assert.Contains("重定向次数", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "HikingGuide-External-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static HttpExternalMediaDownloadService Create(string root, Func<HttpRequestMessage, HttpResponseMessage> respond,
        ExternalMediaDownloadProfile? profile = null) =>
        new(new ApplicationDataPaths(root), profile: profile, testHandler: new FakeHandler(respond),
            resolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));
    private static HttpResponseMessage Image(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return response;
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
