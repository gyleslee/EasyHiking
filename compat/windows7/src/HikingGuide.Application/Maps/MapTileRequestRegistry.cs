using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

/// <summary>每个槽位使用独立虚拟URL，避免同主机甚至同URL图源串用鉴权信息。旧URL不会映射到新配置。</summary>
public sealed class MapTileRequestRegistry
{
    public static readonly HttpRequestOptionsKey<TileCacheRequest> CacheRequestKey = new("HikingGuide.TileCache");
    public static readonly HttpRequestOptionsKey<RasterReprojectionRequest> ReprojectionRequestKey = new("HikingGuide.RasterReprojection");
    public static readonly HttpRequestOptionsKey<OfflineTileRequest> OfflineRequestKey = new("HikingGuide.OfflineTile");
    private readonly Dictionary<string,(MapSourceDefinition Source,IReadOnlyList<string> Templates)> _profiles=[];
    public IReadOnlyList<RasterLayerSlot> Register(IReadOnlyList<RasterLayerSlot> slots)
    {
        var active=new HashSet<string>();
        var registered=slots.Select(slot=>
        {
            if(slot.Source is null) return slot;
            var key=slot.Slot+"-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(slot.Source))));
            active.Add(key); _profiles[key]=(slot.Source.DeepCopy(),slot.Templates.ToArray());
            return slot with {Templates=slot.Templates.Select((_,i)=>$"https://hikingguide-tiles.local/{key}/{i}/{{z}}/{{x}}/{{y}}").ToArray()};
        }).ToArray();
        foreach(var key in _profiles.Keys.Where(k=>!active.Contains(k)).ToArray()) _profiles.Remove(key);
        return registered;
    }
    public HttpRequestMessage? Resolve(Uri uri)
    {
        if(uri.Host!="hikingguide-tiles.local"||uri.Scheme!="https") return null;
        var p=uri.AbsolutePath.Trim('/').Split('/');
        if(p.Length!=5||!_profiles.TryGetValue(p[0],out var profile)||!int.TryParse(p[1],out var server)||server<0||server>=profile.Templates.Count||!int.TryParse(p[2],out var z)||!int.TryParse(p[3],out var x)||!int.TryParse(p[4],out var y)||z<0||z>24||x<0||y<0||x>=(1<<z)||y>=(1<<z)) return null;
        var request=CreateRequest(profile.Source,MapTileTemplateParser.BuildTileUrl(profile.Templates[server],z,x,y));
        request.Options.Set(OfflineRequestKey,new(OfflineMapSourceIdentity.Fingerprint(profile.Source),z,x,y));
        var identity=JsonSerializer.Serialize(new {profile.Source.UrlTemplate,profile.Source.UserAgent,profile.Source.Referer,
            Headers=profile.Source.Headers.OrderBy(h=>h.Key,StringComparer.OrdinalIgnoreCase).ToArray()});
        var config=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        request.Options.Set(CacheRequestKey,new(profile.Source.Id,$"{config}/{z}/{x}/{y}",(long)profile.Source.CacheCapacityMb*1024*1024));
        if(profile.Source.CoordinateSystem!=MapSourceCoordinateSystem.Wgs84)
            request.Options.Set(ReprojectionRequestKey,new(profile.Source.DeepCopy(),profile.Templates[server],config,z,x,y));
        return request;
    }
    public static HttpRequestMessage CreateRequest(MapSourceDefinition source,string url)
    {
        var request=new HttpRequestMessage(HttpMethod.Get,url);
        if(!string.IsNullOrWhiteSpace(source.UserAgent)) request.Headers.TryAddWithoutValidation("User-Agent",source.UserAgent);
        if(!string.IsNullOrWhiteSpace(source.Referer)) request.Headers.Referrer=new Uri(source.Referer);
        foreach(var header in source.Headers) request.Headers.TryAddWithoutValidation(header.Key,header.Value);
        return request;
    }
}
public sealed record TileCacheRequest(Guid SourceId,string Key,long CapacityBytes);
public sealed record RasterReprojectionRequest(MapSourceDefinition Source,string Template,string RawCacheIdentity,int Zoom,int X,int Y);
