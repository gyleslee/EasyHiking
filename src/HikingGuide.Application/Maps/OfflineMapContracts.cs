using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HikingGuide.Core.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;
public sealed record OfflineTileData(byte[] Bytes,byte TileType,string ContentType);
public interface IOfflineMapTileFetcher
{
    Task<OfflineTileData> FetchAsync(MapSourceDefinition source,OfflineTileAddress address,CancellationToken token,Func<CancellationToken,Task>? beforeNetwork=null);
    byte[] NormalizePng(byte[] bytes);
}
public static class OfflineMapSourceIdentity
{
    public static string RawCache(MapSourceDefinition source)=>Hash(JsonSerializer.Serialize(new {source.UrlTemplate,source.UserAgent,source.Referer,Headers=source.Headers.OrderBy(h=>h.Key,StringComparer.OrdinalIgnoreCase).ToArray()}));
    public static string Fingerprint(MapSourceDefinition source)=>Hash($"offline-raster-wgs84-v1/{source.CoordinateSystem}/{RawCache(source)}");
    private static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string? DownloadRestriction(MapSourceDefinition source)
    {
        var templates=MapTileTemplateParser.Parse(source.UrlTemplate).NormalizedTemplates;
        if(templates.Any(t=>new Uri(MapTileTemplateParser.BuildTileUrl(t,0,0,0)).Host.EndsWith("tile.openstreetmap.org",StringComparison.OrdinalIgnoreCase)))return "该OSM公共图源禁止批量下载，请选择允许离线下载的图源。";
        return null;
    }
}
public sealed record OfflineTileRequest(string Fingerprint,int Zoom,int X,int Y);
