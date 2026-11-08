using System.Net;
using HikingGuide.Infrastructure.Maps;

namespace HikingGuide.Infrastructure.Tests;

public sealed class MapTileHttpTransportTests
{
    [Theory]
    [InlineData(301)] [InlineData(302)] [InlineData(303)] [InlineData(307)] [InlineData(308)]
    public async Task RedirectToHttpsReturnsImageAndPreservesSameHostHeaders(int status)
    {
        var calls = new List<Uri>();
        using var client = new HttpClient(new Handler(r =>
        {
            calls.Add(r.RequestUri!);
            Assert.Equal("map-secret", r.Headers.GetValues("X-Map-Key").Single());
            Assert.Equal(HttpMethod.Get, r.Method);
            return calls.Count == 1 ? Redirect(status, "https://tiles.example/tile/13/3530/6671")
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent([255, 216, 255]) };
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://tiles.example/tile/13/3530/6671");
        request.Headers.Add("X-Map-Key", "map-secret");
        using var response = await MapTileHttpTransport.SendAsync(client, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 255, 216, 255 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(new[] { "http", "https" }, calls.Select(u => u.Scheme));
    }

    [Theory]
    [InlineData("https://cdn.example/tile")] [InlineData("https://tiles.example:8443/tile")]
    public async Task CrossAuthorityStripsCredentialsAndReferer(string destination)
    {
        var count = 0;
        using var client = new HttpClient(new Handler(r =>
        {
            if (++count == 1) return Redirect(302, destination);
            Assert.False(r.Headers.Contains("Authorization"));
            Assert.False(r.Headers.Contains("Cookie"));
            Assert.False(r.Headers.Contains("X-Map-Key"));
            Assert.Null(r.Headers.Referrer);
            Assert.Equal("HikingGuide/1", r.Headers.UserAgent.ToString());
            return new(HttpStatusCode.OK);
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://tiles.example/tile");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
        request.Headers.TryAddWithoutValidation("Cookie", "token=secret");
        request.Headers.Add("X-Map-Key", "secret");
        request.Headers.Referrer = new("https://referer.example/?key=secret");
        request.Headers.UserAgent.ParseAdd("HikingGuide/1");
        using var response = await MapTileHttpTransport.SendAsync(client, request);
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData("http://tiles.example/tile")] [InlineData("file:///F:/private.txt")]
    [InlineData("https://user:password@tiles.example/tile")]
    public async Task UnsafeRedirectIsRejectedWithoutSecondRequest(string destination)
    {
        var count = 0;
        using var client = new HttpClient(new Handler(_ => { count++; return Redirect(302, destination); }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://tiles.example/tile");
        await Assert.ThrowsAsync<HttpRequestException>(() => MapTileHttpTransport.SendAsync(client, request));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RelativeRedirectAndLoopLimitAreHandled()
    {
        var count = 0;
        using var client = new HttpClient(new Handler(r =>
        {
            count++;
            Assert.Equal("tiles.example", r.RequestUri!.Host);
            return Redirect(301, "../tile/loop");
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://tiles.example/tile/start");
        await Assert.ThrowsAsync<HttpRequestException>(() => MapTileHttpTransport.SendAsync(client, request));
        Assert.Equal(6, count);
    }

    [Fact]
    public async Task MissingLocationFailsAndHttpErrorsRemainVisible()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://tiles.example/tile");
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.MovedPermanently)));
        await Assert.ThrowsAsync<HttpRequestException>(() => MapTileHttpTransport.SendAsync(client, request));
        using var second = new HttpRequestMessage(HttpMethod.Get, "https://tiles.example/tile");
        using var errorClient = new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden)));
        using var response = await MapTileHttpTransport.SendAsync(errorClient, second);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CancellationStopsRedirectChain()
    {
        using var stop = new CancellationTokenSource();
        var count = 0;
        using var client = new HttpClient(new Handler(_ => { count++; stop.Cancel(); return Redirect(302, "/next"); }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://tiles.example/tile");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MapTileHttpTransport.SendAsync(client, request, stop.Token));
        Assert.Equal(1, count);
    }

    private static HttpResponseMessage Redirect(int status, string location)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.Location = new(location, UriKind.RelativeOrAbsolute);
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handle(request));
    }
}
