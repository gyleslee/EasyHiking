using System.IO;
using System.Net.Http;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Maps;
using SkiaSharp;
namespace HikingGuide.App.Services;

internal sealed class OfflineRasterTileFetcher(OfflineMapLibrary library,MapTileDiskCache cache):IOfflineMapTileFetcher,IDisposable
{
    private readonly HttpClient _http=MapTileHttpTransport.CreateClient();
    private readonly RasterTileReprojectionService _correction=new(cache);
    public async Task<OfflineTileData> FetchAsync(MapSourceDefinition source,OfflineTileAddress address,CancellationToken token,Func<CancellationToken,Task>? beforeNetwork=null)
    {
        var restriction=OfflineMapSourceIdentity.DownloadRestriction(source);if(restriction is not null)throw new InvalidDataException(restriction);
        var existing=await library.ReadTileAsync(OfflineMapSourceIdentity.Fingerprint(source),address,token).ConfigureAwait(false);if(existing is not null)return existing;
        var templates=MapTileTemplateParser.Parse(source.UrlTemplate).NormalizedTemplates;var identity=OfflineMapSourceIdentity.RawCache(source);var capacity=(long)source.CacheCapacityMb*1024*1024;
        cache.Configure(source.Id,capacity);
        async Task<CachedMapTile> Download(HttpRequestMessage message)
        {
            using(message)
            {
                if(beforeNetwork is not null)await beforeNetwork(token).ConfigureAwait(false);
                using var response=await MapTileHttpTransport.SendAsync(_http,message,token).ConfigureAwait(false);
                if(!response.IsSuccessStatusCode)throw new HttpRequestException("离线瓦片下载失败",null,response.StatusCode);
                if(response.Headers.CacheControl?.NoStore==true)throw new InvalidDataException("该图源响应禁止本地保存，未保存离线瓦片。");
                await using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);using var output=new MemoryStream();var buffer=new byte[65536];int n;
                while((n=await stream.ReadAsync(buffer,token).ConfigureAwait(false))>0){if(output.Length+n>8*1024*1024)throw new InvalidDataException("地图瓦片超过8MB限制。");await output.WriteAsync(buffer.AsMemory(0,n),token).ConfigureAwait(false);}
                var now=DateTimeOffset.UtcNow;var expires=response.Headers.CacheControl?.MaxAge is {} age?now+age:response.Content.Headers.Expires??now.AddHours(24);
                return new(output.ToArray(),response.Content.Headers.ContentType?.MediaType??"image/png",expires,response.Headers.CacheControl?.NoCache!=true);
            }
        }
        Exception? last=null;
        foreach(var template in templates)
        {
            try
            {
                CachedMapTile tile;
                if(source.CoordinateSystem==MapSourceCoordinateSystem.Wgs84)
                {tile=await cache.GetAsync(source.Id,$"{identity}/{address.Zoom}/{address.X}/{address.Y}",capacity,()=>Download(MapTileRequestRegistry.CreateRequest(source,MapTileTemplateParser.BuildTileUrl(template,address.Zoom,address.X,address.Y))),token).ConfigureAwait(false);}
                else tile=await _correction.GetAsync(new(source,template,identity,address.Zoom,address.X,address.Y),Download,token).ConfigureAwait(false);
                using var image=SKImage.FromEncodedData(tile.Bytes)??throw new InvalidDataException("图源返回的内容不是有效图片。");if(image.Width is <1 or >2048||image.Height is <1 or >2048)throw new InvalidDataException("瓦片图片尺寸超出支持范围。");
                var type=ImageType(tile.Bytes);if(type==0)return new(NormalizePng(tile.Bytes),2,"image/png");
                return new(tile.Bytes,type,type==2?"image/png":type==3?"image/jpeg":"image/webp");
            }
            catch(HttpRequestException e){last=e;if((int?)e.StatusCode is 401 or 403 or 429)throw;}
        }
        throw last??new IOException("没有可用的图源服务器。");
    }
    public byte[] NormalizePng(byte[] bytes)
    {using var image=SKImage.FromEncodedData(bytes)??throw new InvalidDataException("无效地图图片。");using var data=image.Encode(SKEncodedImageFormat.Png,100);return data.ToArray();}
    private static byte ImageType(byte[] bytes)=>bytes.Length>=8&&bytes[0]==137&&bytes[1]==80&&bytes[2]==78&&bytes[3]==71?(byte)2:bytes.Length>=3&&bytes[0]==255&&bytes[1]==216&&bytes[2]==255?(byte)3:bytes.Length>=12&&System.Text.Encoding.ASCII.GetString(bytes,8,4)=="WEBP"?(byte)4:(byte)0;
    public void Dispose()=>_http.Dispose();
}
