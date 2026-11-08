using System.Diagnostics;
using System.Net.Http.Headers;
using HikingGuide.Application.Logging;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.Maps;

public sealed class MapSourceTestService(
    HttpClient httpClient,
    MapSourceTemplateService templateService,
    ILoggingService logging) : IMapSourceTestService
{
    public async Task<MapSourceTestResult> TestAsync(MapSourceDefinition source, CancellationToken cancellationToken = default)
    {
        var validation = templateService.Validate(source);
        if (!validation.IsValid) return new(false, null, string.Join(Environment.NewLine, validation.Errors), TimeSpan.Zero);
        var parsed=MapTileTemplateParser.Parse(source.UrlTemplate);
        var results=new List<MapServerTestResult>();
        var zoom = Math.Clamp(Math.Max(source.MinZoom, 1), source.MinZoom, source.MaxZoom);
        var tile = 1 << Math.Max(0, zoom - 1);
        for(var i=0;i<parsed.NormalizedTemplates.Count;i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var single=await TestServerAsync(source,MapTileTemplateParser.BuildTileUrl(parsed.NormalizedTemplates[i],zoom,tile,tile),cancellationToken);
            results.Add(new(parsed.ServerTokens[i],single.IsSuccess,single.HttpStatusCode,single.Message,single.Duration));
        }
        var success=results.All(r=>r.IsSuccess);
        var summary=success?"图源测试成功。":results.Any(r=>r.IsSuccess)?"图源部分服务器不可用。":"图源所有服务器均不可用。";
        return new(success,results.Count==1?results[0].HttpStatusCode:null,summary+Environment.NewLine+string.Join(Environment.NewLine,results.Select(r=>$"{r.ServerToken}：{r.Message}")),TimeSpan.FromTicks(results.Sum(r=>r.Duration.Ticks))) {Servers=results};
    }
    private async Task<MapSourceTestResult> TestServerAsync(MapSourceDefinition source,string url,CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(source.UserAgent)) request.Headers.TryAddWithoutValidation("User-Agent", source.UserAgent);
        if (Uri.TryCreate(source.Referer, UriKind.Absolute, out var referer)) request.Headers.Referrer = referer;
        foreach (var header in source.Headers)
        {
            if (SensitiveDataRedactor.IsSensitiveHeader(header.Key))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            else
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var timer = Stopwatch.StartNew();
        try
        {
            using var response = await MapTileHttpTransport.SendAsync(httpClient, request, cancellationToken);
            timer.Stop();
            if (response.IsSuccessStatusCode)
            {
                logging.Information($"[地图] 图源测试成功：{source.Name}，地址：{SensitiveDataRedactor.RedactUrl(url)}");
                return new(true, (int)response.StatusCode, "图源测试成功。", timer.Elapsed);
            }
            logging.Warning($"[地图] XYZ瓦片请求失败。图源：{source.Name}，状态：{(int)response.StatusCode}，地址：{SensitiveDataRedactor.RedactUrl(url)}");
            return new(false, (int)response.StatusCode, $"无法读取地图瓦片。HTTP状态：{(int)response.StatusCode}", timer.Elapsed);
        }
        catch (HttpRequestException error)
        {
            timer.Stop();
            logging.Error(new HttpRequestException("地图图源网络请求失败（敏感信息已省略）。"), $"[地图] 图源测试网络错误：{source.Name}，地址：{SensitiveDataRedactor.RedactUrl(url)}");
            var reason=error.HttpRequestError switch {HttpRequestError.NameResolutionError=>"DNS域名解析失败",HttpRequestError.SecureConnectionError=>"TLS安全连接失败",HttpRequestError.ConnectionError=>"网络连接失败",_=>"网络请求失败"};
            return new(false, null, $"无法读取地图瓦片：{reason}。", timer.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timer.Stop();
            logging.Warning($"[地图] 图源测试超时：{source.Name}，地址：{SensitiveDataRedactor.RedactUrl(url)}");
            return new(false, null, "读取地图瓦片超时，请检查网络连接或图源响应速度。", timer.Elapsed);
        }
    }
}
