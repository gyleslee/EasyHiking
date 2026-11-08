using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Maps;
using HikingGuide.Infrastructure.Maps;
using SkiaSharp;

namespace HikingGuide.App.Services;

/// <summary>在线图片解码/编码适配器；几何转换及重采样在Core中独立测试。</summary>
internal sealed class RasterTileReprojectionService(MapTileDiskCache cache)
{
    private readonly SemaphoreSlim _gate=new(2,2);
    public Task<CachedMapTile> GetAsync(RasterReprojectionRequest request,Func<HttpRequestMessage,Task<CachedMapTile>> download,CancellationToken token)
    {
        var source=request.Source;var capacity=(long)source.CacheCapacityMb*1024*1024;
        var key=$"{RasterTileReprojection.AlgorithmVersion}/{source.CoordinateSystem}/{request.RawCacheIdentity}/{request.Zoom}/{request.X}/{request.Y}";
        return cache.GetAsync(source.Id,key,capacity,async()=>
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var plan=new RasterTileReprojection(request.Zoom,request.X,request.Y,source.CoordinateSystem,token);
                var images=await Task.WhenAll(plan.RequiredTiles.Select(async address=>
                {
                    var rawKey=$"{request.RawCacheIdentity}/{address.Zoom}/{address.X}/{address.Y}";
                    var tile=await cache.GetAsync(source.Id,rawKey,capacity,async()=>
                    {
                        using var message=MapTileRequestRegistry.CreateRequest(source,MapTileTemplateParser.BuildTileUrl(request.Template,address.Zoom,address.X,address.Y));
                        return await download(message).ConfigureAwait(false);
                    },token).ConfigureAwait(false);
                    return (Address:address,Image:Decode(tile.Bytes),Metadata:tile);
                })).ConfigureAwait(false);
                var rgba=plan.Render(images.ToDictionary(v=>v.Address,v=>v.Image),token);
                using var bitmap=new SKBitmap(new SKImageInfo(256,256,SKColorType.Rgba8888,SKAlphaType.Premul));
                Marshal.Copy(rgba,0,bitmap.GetPixels(),rgba.Length);
                using var encoded=bitmap.Encode(SKEncodedImageFormat.Png,100);
                return new CachedMapTile(encoded.ToArray(),"image/png",images.Min(v=>v.Metadata.ExpiresAt),images.All(v=>v.Metadata.CanCache));
            }
            finally{_gate.Release();}
        },token);
    }
    internal static RasterTileImage Decode(byte[] bytes)
    {
        using var image=SKImage.FromEncodedData(bytes)??throw new InvalidDataException("图源返回的内容不是有效地图图片。");
        if(image.Width is <1 or >2048||image.Height is <1 or >2048)throw new InvalidDataException("图源瓦片图片尺寸超出支持范围。");
        using var bitmap=new SKBitmap(new SKImageInfo(image.Width,image.Height,SKColorType.Rgba8888,SKAlphaType.Premul));
        if(!image.ReadPixels(bitmap.Info,bitmap.GetPixels(),bitmap.RowBytes,0,0))throw new InvalidDataException("地图图片解码失败。");
        var pixels=new byte[image.Width*image.Height*4];Marshal.Copy(bitmap.GetPixels(),pixels,0,pixels.Length);
        return new(image.Width,image.Height,pixels);
    }
}
