using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HikingGuide.Application.Routing;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.Routing;

public sealed class BRouterProvider : IRoutingProvider
{
    private readonly BRouterProcessService _process;
    private readonly Func<string> _datasetId;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim? _routeGate;
    public const string FootShortestProfile = "shortest";

    public BRouterProvider(BRouterProcessService process, Func<string> datasetId,
        HttpClient? client = null, SemaphoreSlim? routeGate = null)
    {
        _process = process;
        _datasetId = datasetId;
        _client = client ?? new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { Timeout = TimeSpan.FromMinutes(2) };
        _routeGate = routeGate;
    }

    public async Task<RoutingResult> RouteFootShortestAsync(GeoCoordinate start,
        GeoCoordinate end, CancellationToken cancellationToken = default)
    {
        if (_routeGate is not null) await _routeGate.WaitAsync(cancellationToken);
        try { return await RouteFootShortestCoreAsync(start, end, cancellationToken); }
        finally { _routeGate?.Release(); }
    }

    private async Task<RoutingResult> RouteFootShortestCoreAsync(GeoCoordinate start,
        GeoCoordinate end, CancellationToken cancellationToken)
    {
        start.Validate(); end.Validate();
        if (!_process.IsRunning) await _process.StartAsync(cancellationToken);
        var query = string.Create(CultureInfo.InvariantCulture,
            $"brouter?lonlats={start.Longitude:R},{start.Latitude:R}|{end.Longitude:R},{end.Latitude:R}&profile={FootShortestProfile}&alternativeidx=0&format=geojson");
        var address = new Uri(_process.BaseAddress!, query);
        using var response = await _client.GetAsync(address, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            var missing = Regex.Match(error, @"(?<![A-Za-z0-9])[EW]\d{1,3}_[NS]\d{1,2}\.rd5",
                RegexOptions.CultureInvariant);
            if (missing.Success)
                throw new InvalidOperationException($"当前区域缺少BRouter路由数据：{missing.Value}。");
            if (error.Contains("no route", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("not found", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("两个规划点之间没有找到可用的BRouter路线。");
            throw new InvalidOperationException($"BRouter路由失败：HTTP {(int)response.StatusCode}，{error.Trim()}");
        }
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        var features = document.RootElement.GetProperty("features");
        var geometry = features.EnumerateArray().Select(item => item.GetProperty("geometry"))
            .FirstOrDefault(item => item.GetProperty("type").GetString() == "LineString");
        if (geometry.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("BRouter没有返回路线几何。");
        var points = new List<GeoCoordinate>();
        foreach (var coordinate in geometry.GetProperty("coordinates").EnumerateArray())
        {
            // 第三个坐标若存在是 BRouter 高程，不能进入最终统计字段。
            var values = coordinate.EnumerateArray().ToArray();
            var point = new GeoCoordinate(values[1].GetDouble(), values[0].GetDouble());
            point.Validate();
            if (points.Count == 0 || points[^1] != point) points.Add(point);
        }
        if (points.Count < 2) throw new InvalidOperationException("BRouter返回的路线点不足。");
        return new RoutingResult(points, FootShortestProfile, _datasetId());
    }
}
