using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HikingGuide.Core.Elevation;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Elevation;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Application.Maps;
using SkiaSharp;

namespace HikingGuide.App.Services;

/// <summary>只读DEM、按XYZ请求后台生成透明PNG；缓存包复用已有原子写入/FIFO/并发去重。</summary>
public sealed class DemTerrainTiles : IDisposable
{
    public static readonly Guid ContourCacheId = Guid.Parse("e8752365-3470-4993-89a8-08964cfae501");
    public static readonly Guid HillshadeCacheId = Guid.Parse("e8752365-3470-4993-89a8-08964cfae502");
    private readonly MapTileDiskCache _cache;
    private readonly DemDatabaseCatalog _catalog;
    private readonly DemElevationProvider _provider;
    private readonly bool _ownsProvider;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,Lazy<Task<double?[,]>>> _grids=new();
    private CancellationTokenSource _sampling=new();
    private string _gridIdentity="";
    private int _sampledGrids;
    public int SampledGrids=>Volatile.Read(ref _sampledGrids);
    private readonly SemaphoreSlim _gate = new(2,2);
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _maintenance=new(1,1);
    private bool _clearingContours,_clearingShade;
    private CancellationTokenSource _generation = new();
    private CancellationTokenSource _shadeGeneration = new();
    private OfflineTerrainSettings _settings = new();
    private string _signature = "";
    private string _shadeSignature="";
    private sealed record RenderResult(byte[] Png,byte[] Labels);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,Lazy<Task<RenderResult>>> _artifacts=new();
    private sealed record Viewport(double West,double South,double East,double North);
    private Viewport? _viewport;
    public void SetViewport(double west,double south,double east,double north)
    {
        if(new[]{west,south,east,north}.All(double.IsFinite)&&south<north)
            _viewport=new(west,south,east,north);
    }
    private void EnsureInView(int z,int x,int y,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(_settings.ScreenAlignedLabels)return; // 3D引擎自行确定投影覆盖，不能按2D范围拒绝。
        var v=_viewport;
        if(v is null||v.West>v.East||v.East-v.West>=360)return;
        var nw=TerrainTileMath.Coordinate(z,x,y,0,0);var se=TerrainTileMath.Coordinate(z,x,y,128,128);
        var center=(v.West+v.East)/2;var shift=360*Math.Round((center-(nw.Longitude+se.Longitude)/2)/360);
        var pad=se.Longitude-nw.Longitude;
        if(se.Longitude+shift<v.West-pad||nw.Longitude+shift>v.East+pad||nw.Latitude<v.South-pad||se.Latitude>v.North+pad)
            throw new OperationCanceledException("地图视野已改变，跳过离屏地形瓦片。");
    }
    public string LastError { get; private set; } = "";
    public int GeneratedTiles { get; private set; }
    public DemTerrainTiles(DemDatabaseCatalog catalog, DemElevationProvider provider, string cacheRoot)
    {_catalog=catalog;_provider=provider;_cache=MapTileDiskCache.Shared(cacheRoot);}
    public DemTerrainTiles(DemDatabaseCatalog catalog,string cacheRoot):this(catalog,new DemElevationProvider(catalog,maximumOpenHandles:4,rasterWindowCacheCapacity:32),cacheRoot)
    {_ownsProvider=true;}
    public static string CacheRoot => Path.Combine(new ApplicationDataPaths().RootDirectory,"OfflineTerrainCache");

    public object Configure(OfflineTerrainSettings? settings)
    {
        lock(_stateGate)
        {
        settings = settings is {IsValid:true} ? settings : new();
        var database = settings.DemDatabaseId is {} id ? _catalog.Databases.FirstOrDefault(d=>d.Id==id) : null;
        var gridIdentity=$"{settings.DemDatabaseId}/{database?.Fingerprint}";
        if(gridIdentity!=_gridIdentity||(!settings.ContoursVisible&&!settings.HillshadeVisible))
        {var old=_sampling;_sampling=new();old.Cancel();_grids.Clear();_gridIdentity=gridIdentity;}
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new {settings.DemDatabaseId,database?.Fingerprint,settings.ContourColor,settings.MajorContoursEnabled,settings.MajorContourIntervalMeters,styleVersion="major-contours-v1",version=TerrainTileMath.AlgorithmVersion}))));
        var shadeSignature=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{settings.DemDatabaseId,database?.Fingerprint,version=HillshadeOverlay.AlgorithmVersion,TerrainTileMath.LightAzimuth,TerrainTileMath.LightAltitude}))));
        if(settings.ScreenAlignedLabels)signature+="-screen-labels-v1";
        if (_signature != signature || settings.ContoursVisible != _settings.ContoursVisible)
        {var old=_generation;_generation=new();old.Cancel();}
        if(_shadeSignature!=shadeSignature||settings.HillshadeVisible!=_settings.HillshadeVisible){var old=_shadeGeneration;_shadeGeneration=new();old.Cancel();}
        _shadeSignature=shadeSignature;
        _signature=signature;_settings=settings;
        _cache.Configure(ContourCacheId,(long)settings.ContourCacheMb*1024*1024);
        _cache.Configure(HillshadeCacheId,(long)settings.HillshadeCacheMb*1024*1024);
        var ready=database is {Status:DemDatabaseStatus.Ready} && Directory.Exists(database.RootPath);
        LastError=ready?"":"请选择已挂载且可读取的DEM数据库。";
        return new {signature,hillshadeSignature=shadeSignature,screenAlignedLabels=settings.ScreenAlignedLabels,color=settings.ContourColor,contours=ready&&settings.ContoursVisible,hillshade=ready&&settings.HillshadeVisible,opacity=settings.HillshadeOpacity,contourOpacity=settings.ContourOpacity};
        }
    }

    public async Task<byte[]> GetAsync(Uri uri)
    {
        var p=uri.AbsolutePath.Trim('/').Split('/');
        OfflineTerrainSettings s;CancellationToken token;CancellationToken sampling;string gridIdentity;string shadeSignature;
        lock(_stateGate)
        {
            if(p.Length!=5 || (p[0]!=_signature&&!(p[1]=="hillshade"&&p[0]==_shadeSignature)))throw new OperationCanceledException("地形设置已改变。");
            if(p[1]=="hillshade"?_clearingShade:_clearingContours)throw new OperationCanceledException("正在清除所选地形缓存。");
            s=_settings;shadeSignature=_shadeSignature;token=p[1]=="hillshade"?_shadeGeneration.Token:_generation.Token;
            sampling=_sampling.Token;gridIdentity=_gridIdentity;
        }
        if(p[1] is not ("contours" or "hillshade" or "labels") ||
            !int.TryParse(p[2],out var z)||!int.TryParse(p[3],out var x)||!int.TryParse(p[4].Replace(".png","",StringComparison.Ordinal).Replace(".json","",StringComparison.Ordinal),out var y)||
            z is <13 or >22 || x<0||y<0||x>=(1<<z)||y>=(1<<z))throw new InvalidDataException("地形瓦片请求无效或已失效。");
        var contour=p[1]!="hillshade";var labels=p[1]=="labels";
        if(s.DemDatabaseId is not {} dem || !(contour?s.ContoursVisible:s.HillshadeVisible)) throw new OperationCanceledException();
        var cacheId=contour?ContourCacheId:HillshadeCacheId;var capacity=(long)(contour?s.ContourCacheMb:s.HillshadeCacheMb)*1024*1024;
        var key=string.Join('/',p);
        if(!contour){p[0]=shadeSignature;key=string.Join('/',p);}
        async Task<CachedMapTile> Generate()
        {
            var artifactKey=$"{p[0]}/{(contour?"contours":"hillshade")}/{z}/{x}/{y}";
            var lazy=_artifacts.GetOrAdd(artifactKey,_=>new(async()=>
            {
                await TerrainGenerationPriority.Shared.WaitForDerivedAsync(token).ConfigureAwait(false);
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await TerrainGenerationPriority.Shared.WaitForDerivedAsync(token).ConfigureAwait(false);EnsureInView(z,x,y,token);
                    var grid=await GetGridAsync(dem,gridIdentity,z,x,y,sampling).WaitAsync(token).ConfigureAwait(false);
                    var rendered=await Task.Run(()=>Render(grid,z,x,y,contour,s.ContourColor,s.ScreenAlignedLabels,s.MajorContoursEnabled,s.MajorContourIntervalMeters,token),token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    GeneratedTiles++;return rendered;
                }
                finally{_gate.Release();}
            }));
            try
            {
                var rendered=await lazy.Value.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if(contour&&!labels&&s.ScreenAlignedLabels)
                    await _cache.GetAsync(cacheId,$"{p[0]}/labels/{z}/{x}/{y}.json",capacity,()=>{token.ThrowIfCancellationRequested();return Task.FromResult(new CachedMapTile(rendered.Labels,"application/json",DateTimeOffset.MaxValue));},token).ConfigureAwait(false);
                return new CachedMapTile(labels?rendered.Labels:rendered.Png,labels?"application/json":"image/png",DateTimeOffset.MaxValue);
            }
            catch{_artifacts.TryRemove(new KeyValuePair<string,Lazy<Task<RenderResult>>>(artifactKey,lazy));throw;}
            finally{if(_artifacts.Count>16)foreach(var item in _artifacts.Where(k=>k.Value.IsValueCreated&&k.Value.Value.IsCompleted).Take(_artifacts.Count-16))_artifacts.TryRemove(item.Key,out _);}
        }
        // 旧客户端即使用等高线签名请求阴影，也只能访问新版规范缓存键，不能回落到旧灰度瓦片。
        return (await _cache.GetAsync(cacheId,key,capacity,Generate,token).ConfigureAwait(false)).Bytes;
    }

    // One-cell halo: hillshade derivatives and contours agree on adjoining world-grid boundaries.
    private async Task<double?[,]> GetGridAsync(Guid dem,string identity,int z,int x,int y,CancellationToken token)
    {
        var key=$"{identity}/{z}/{x}/{y}";
        var lazy=_grids.GetOrAdd(key,_=>new(()=>Task.Run(()=>ReadGrid(dem,z,x,y,token),token)));
        try{return await lazy.Value.ConfigureAwait(false);}
        catch{_grids.TryRemove(new KeyValuePair<string,Lazy<Task<double?[,]>>>(key,lazy));throw;}
        finally{if(_grids.Count>16)foreach(var item in _grids.Where(p=>p.Value.IsValueCreated&&p.Value.Value.IsCompleted).Take(_grids.Count-16))_grids.TryRemove(item.Key,out _);}
    }
    private double?[,] ReadGrid(Guid dem,int z,int x,int y,CancellationToken token)
    {
        const int n=TerrainTileMath.GridCells;
        var grid=new double?[n+3,n+3];
        var longitudes=Enumerable.Range(-1,n+3).Select(gx=>{var lon=TerrainTileMath.Coordinate(z,x,y,gx,0).Longitude;return ((lon+180)%360+360)%360-180;}).ToArray();
        var coordinates=new GeoCoordinate[n+3];
        for(var gy=-1;gy<=n+1;gy++)
        {
            EnsureInView(z,x,y,token);
            var latitude=TerrainTileMath.Coordinate(z,x,y,0,gy).Latitude;
            for(var i=0;i<coordinates.Length;i++)coordinates[i]=new(latitude,longitudes[i]);
            var samples=_provider.SampleDenseRow(dem,coordinates,token);
            for(var i=0;i<samples.Length;i++)
            {
                var sample=samples[i];
                if(sample.Status is DemSampleStatus.DatabaseDisconnected or DemSampleStatus.InvalidUnit)
                    throw new IOException("DEM不可读取或高程单位未确认，请在DEM管理中检查。");
                if(sample.Status==DemSampleStatus.NoData && sample.QualityFlag is {Length:>0})throw new IOException("DEM读取失败，请检查文件完整性后重新扫描数据库。");
                grid[gy+1,i]=sample.ElevationMeters;
            }
        }
        Interlocked.Increment(ref _sampledGrids);return grid;
    }
    private RenderResult Render(double?[,] grid,int z,int x,int y,bool contour,string color,bool screenLabels,bool majorEnabled,int majorInterval,CancellationToken token)
    {
        const int n=TerrainTileMath.GridCells;
        using var bitmap=new SKBitmap(512,512,SKColorType.Rgba8888,SKAlphaType.Premul);
        using var canvas=new SKCanvas(bitmap);canvas.Clear(SKColors.Transparent);
        var outputLabels=new List<object>();
        if(contour) DrawContours(canvas,grid,TerrainTileMath.ContourInterval(z),SKColor.Parse(color),screenLabels,majorEnabled,majorInterval,(point,elevation)=>
        {var c=TerrainTileMath.Coordinate(z,x,y,point.X/4,point.Y/4);outputLabels.Add(new{latitude=c.Latitude,longitude=c.Longitude,elevation});},token);
        else
        {
            // 在网格节点上计算，含相邻瓦片共用的最后一行/列；半像素源矩形保持世界坐标对齐。
            using var shade=new SKBitmap(n+1,n+1,SKColorType.Rgba8888,SKAlphaType.Premul);
            shade.Erase(SKColors.Transparent);
            for(var gy=0;gy<=n;gy++)
            {
                token.ThrowIfCancellationRequested();
                var step=TerrainTileMath.GroundStep(z,TerrainTileMath.Coordinate(z,x,y,0,gy).Latitude);
                for(var gx=0;gx<=n;gx++)
                {
                    var row=gy+1;var col=gx+1;
                    if(grid[row,col] is null||grid[row,col-1] is not {} w||grid[row,col+1] is not {} e||grid[row-1,col] is not {} north||grid[row+1,col] is not {} south)continue;
                    var alpha=(byte)Math.Round(255*HillshadeOverlay.Opacity(w,e,north,south,step));
                    shade.SetPixel(gx,gy,new SKColor(0,0,0,alpha));
                }
            }
            using var image=SKImage.FromBitmap(shade);
            var source=new SKRect(.5f,.5f,n+.5f,n+.5f);
            var destination=new SKRect(0,0,512,512);
#if WIN7_COMPAT
            using var filter=new SKPaint{FilterQuality=SKFilterQuality.Low};
            canvas.DrawImage(image,source,destination,filter);
#else
            canvas.DrawImage(image,source,destination,new SKSamplingOptions(SKFilterMode.Linear,SKMipmapMode.None));
#endif
        }
        using var png=SKImage.FromBitmap(bitmap);using var encoded=png.Encode(SKEncodedImageFormat.Png,100);return new(encoded.ToArray(),JsonSerializer.SerializeToUtf8Bytes(outputLabels));
    }

    private static void DrawContours(SKCanvas canvas,double?[,] grid,int interval,SKColor color,bool screenLabels,bool majorEnabled,int majorInterval,Action<SKPoint,double> label,CancellationToken token)
    {
        using var paint=new SKPaint{Color=color,Style=SKPaintStyle.Stroke,StrokeWidth=(float)TerrainTileMath.NormalContourStrokeWidth,IsAntialias=true};
        using var font=new SKFont(SKTypeface.Default,22);
        using var halo=new SKPaint{Color=SKColors.White,Style=SKPaintStyle.Stroke,StrokeWidth=4,IsAntialias=true};
        using var ink=new SKPaint{Color=color,IsAntialias=true};
        var labels=new List<SKRect>();
        foreach(var line in TerrainTileMath.Contours(grid,interval,token))
        {
            token.ThrowIfCancellationRequested();
            paint.StrokeWidth=(float)TerrainTileMath.ContourStrokeWidth(line.Elevation,majorEnabled,majorInterval);
            using var path=new SKPath();var points=line.Points.Select(p=>new SKPoint((float)((p.X-1)*4),(float)((p.Y-1)*4))).ToArray();
            path.MoveTo(points[0]);foreach(var point in points.Skip(1))path.LineTo(point);
            canvas.DrawPath(path,paint);
            var length=0d;for(var i=1;i<points.Length;i++)length+=Math.Sqrt(Math.Pow(points[i].X-points[i-1].X,2)+Math.Pow(points[i].Y-points[i-1].Y,2));
            if(length<130)continue;
            var text=line.Elevation.ToString("0",CultureInfo.InvariantCulture);
#if WIN7_COMPAT
            var glyphs=new ushort[font.CountGlyphs(text)];font.GetGlyphs(text,glyphs);var width=font.MeasureText(glyphs);
#else
            var width=font.MeasureText(text);
#endif
            // Walk by arclength, not vertex index; regular spacing in dense/flat terrain alike.
            var walked=0d;var anchor=points[0];
            for(var i=1;i<points.Length;i++)
            {
                var d=Math.Sqrt(Math.Pow(points[i].X-points[i-1].X,2)+Math.Pow(points[i].Y-points[i-1].Y,2));
                if(walked+d>=length/2){var f=(float)((length/2-walked)/d);anchor=new(points[i-1].X+f*(points[i].X-points[i-1].X),points[i-1].Y+f*(points[i].Y-points[i-1].Y));break;}walked+=d;
            }
            var box=new SKRect(anchor.X-width/2-5,anchor.Y-24,anchor.X+width/2+5,anchor.Y+8);
            if(box.Left<5||box.Top<5||box.Right>507||box.Bottom>507||labels.Any(b=>b.IntersectsWith(box)))continue;
            labels.Add(box);label(anchor,line.Elevation);
            if(!screenLabels){canvas.DrawText(text,anchor.X-width/2,anchor.Y,font,halo);canvas.DrawText(text,anchor.X-width/2,anchor.Y,font,ink);}
        }
    }
    public async Task ClearCacheAsync(bool contours)
    {
        await _maintenance.WaitAsync().ConfigureAwait(false);
        try
        {
            lock(_stateGate)
            {
                if(contours){_clearingContours=true;var old=_generation;_generation=new();old.Cancel();}
                else{_clearingShade=true;var old=_shadeGeneration;_shadeGeneration=new();old.Cancel();}
            }
            var kind=contours?"/contours/":"/hillshade/";
            foreach(var key in _artifacts.Keys.Where(k=>k.Contains(kind,StringComparison.Ordinal)))_artifacts.TryRemove(key,out _);
            await _cache.ClearAsync(contours?ContourCacheId:HillshadeCacheId).ConfigureAwait(false);
        }
        finally
        {
            lock(_stateGate){if(contours)_clearingContours=false;else _clearingShade=false;}
            _maintenance.Release();
        }
    }
    public void Dispose()
    {
        lock(_stateGate){_generation.Cancel();_shadeGeneration.Cancel();_sampling.Cancel();_grids.Clear();}
        if(_ownsProvider)_provider.Dispose();
    }
}
