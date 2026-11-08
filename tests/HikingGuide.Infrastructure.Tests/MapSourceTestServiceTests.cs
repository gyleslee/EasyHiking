using System.Net;
using HikingGuide.Application.Logging;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Maps;

namespace HikingGuide.Infrastructure.Tests;

public sealed class MapSourceTestServiceTests
{
    [Fact]
    public async Task TestAsync_GoogleAliasTestsAllFourUnpaddedServers()
    {
        var hosts = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            hosts.Add(request.RequestUri!.Host);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var log = new TestLog();
        var source = CreateSource("http://mt{$s}.google.com/vt/lyrs=y&hl=zh-CN&gl=cn&scale=2&x={x}&y={y}&z={z}");
        var result = await new MapSourceTestService(client, new MapSourceTemplateService(), log).TestAsync(source);
        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "mt0.google.com", "mt1.google.com", "mt2.google.com", "mt3.google.com" }, hosts);
        Assert.Equal(new[] { "0", "1", "2", "3" }, result.Servers.Select(s => s.ServerToken));
    }

    [Fact]
    public async Task TestAsync_ReportsHttp403WithoutThrowing()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var log = new TestLog();
        var service = new MapSourceTestService(client, new MapSourceTemplateService(), log);

        var result = await service.TestAsync(CreateSource("https://tiles.example/{z}/{x}/{y}.png?token=secret"));

        Assert.False(result.IsSuccess);
        Assert.Equal(403, result.HttpStatusCode);
        Assert.Contains("403", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Messages, value => value.Contains("secret", StringComparison.Ordinal));
        Assert.Contains(log.Messages, value => value.Contains("token=%2A%2A%2A", StringComparison.OrdinalIgnoreCase) || value.Contains("token=***", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestAsync_NetworkFailureReturnsChineseMessage()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")));
        using var log = new TestLog();
        var service = new MapSourceTestService(client, new MapSourceTemplateService(), log);

        var result = await service.TestAsync(CreateSource("https://tiles.example/{zoom}/{x}/{y}.png"));

        Assert.False(result.IsSuccess);
        Assert.Null(result.HttpStatusCode);
        Assert.Contains("网络", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestAsync_AppliesUserAgentRefererAndCustomHeader()
    {
        HttpRequestMessage? captured = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var log = new TestLog();
        var source = CreateSource("https://tiles.example/{z}/{x}/{y}.png");
        source.UserAgent = "HikingGuide.Tests/1";
        source.Referer = "https://referer.example/";
        source.Headers["X-Map-Test"] = "中文值";

        var result = await new MapSourceTestService(client, new MapSourceTemplateService(), log).TestAsync(source);

        Assert.True(result.IsSuccess);
        Assert.Equal("HikingGuide.Tests/1", captured!.Headers.UserAgent.ToString());
        Assert.Equal("https://referer.example/", captured.Headers.Referrer!.ToString());
        Assert.Equal("中文值", Assert.Single(captured.Headers.GetValues("X-Map-Test")));
    }

    private static MapSourceDefinition CreateSource(string url) => new()
    {
        Name = "测试图源",
        UrlTemplate = url,
        MinZoom = 0,
        MaxZoom = 19,
        Opacity = 1
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class TestLog : ILoggingService
    {
        public List<string> Messages { get; } = [];
        public void Debug(string message) => Messages.Add(message);
        public void Information(string message) => Messages.Add(message);
        public void Warning(string message) => Messages.Add(message);
        public void Error(Exception exception, string message) => Messages.Add(message);
        public void Dispose() { }
    }
}
