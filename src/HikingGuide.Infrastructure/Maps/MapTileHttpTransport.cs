using System.Net;

namespace HikingGuide.Infrastructure.Maps;

/// <summary>地图测试与实际加载共用的有限次重定向；跨站不转发图源鉴权头。</summary>
public static class MapTileHttpTransport
{
    public static HttpClient CreateClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(15) };

    /// <remarks>传入的客户端须关闭自动重定向；返回的响应由调用方释放。</remarks>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        const int maximumRedirects = 5;
        var current = request;
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await client.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                    return response;

                Uri target;
                try
                {
                    if (redirects >= maximumRedirects)
                        throw new HttpRequestException("地图瓦片重定向次数超过限制。");
                    var location = response.Headers.Location;
                    if (location is null || !Uri.TryCreate(current.RequestUri!, location, out var next)
                        || next.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(next.UserInfo))
                        throw new HttpRequestException("地图瓦片重定向地址无效。");
                    if (current.RequestUri!.Scheme == "https" && next.Scheme != "https")
                        throw new HttpRequestException("地图瓦片不允许从HTTPS重定向到HTTP。");
                    target = next;
                }
                finally { response.Dispose(); }

                var nextRequest = new HttpRequestMessage(HttpMethod.Get, target);
                var previous = current.RequestUri!;
                var sameAuthority = previous.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase)
                    && (previous.Scheme == target.Scheme && previous.Port == target.Port
                        || previous.Scheme == "http" && previous.IsDefaultPort
                        && target.Scheme == "https" && target.IsDefaultPort);
                foreach (var header in current.Headers)
                {
                    if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                    if (sameAuthority || header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
                        || header.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase))
                        nextRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                if (!ReferenceEquals(current, request)) current.Dispose();
                current = nextRequest;
            }
        }
        finally
        {
            if (!ReferenceEquals(current, request)) current.Dispose();
        }
    }
}
