using System.Net;
using System.Text.RegularExpressions;
using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.Routing;

/// <summary>从官方 segments4 目录实际列出的 rd5 建立可下载集合，避免生成假 URL。</summary>
public sealed class WorldTileCatalog(HttpClient? client = null)
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<IReadOnlySet<BRouterTile>> GetAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync(RoutingDataStore.OfficialBaseUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = new HashSet<BRouterTile>();
        foreach (Match match in Regex.Matches(html, "href=\"([EW][0-9]{1,3}_[NS][0-9]{1,2}\\.rd5)\"",
                     RegexOptions.CultureInvariant))
        {
            var candidate = WebUtility.HtmlDecode(match.Groups[1].Value);
            try { result.Add(BRouterTile.ParseFileName(candidate)); }
            catch (FormatException) { }
        }
        if (result.Count == 0)
            throw new InvalidDataException("官方BRouter目录未提供可用的路由瓦片列表。");
        return result;
    }
}
