using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using HikingGuide.App.Services;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Elevation;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Elevation;
using HikingGuide.Infrastructure.Maps;
using Microsoft.Web.WebView2.Wpf;
using SkiaSharp;

namespace HikingGuide.App;

public partial class App
{
    private async Task<bool> RunHillshadeSmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();string? error=null;object? metrics=null;
        var folder=Path.GetDirectoryName(output)!;
        try
        {
            var database=DemCatalog.Databases.FirstOrDefault(d=>d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase))
                ??DemCatalog.Databases.First(d=>d.Status==DemDatabaseStatus.Ready);
            if(DemProvider is null)throw new InvalidOperationException("真实DEM未就绪。");
            var service=new DemTerrainTiles(DemCatalog,DemProvider,DemTerrainTiles.CacheRoot);
            var options=new OfflineTerrainSettings{DemDatabaseId=database.Id,ContoursVisible=true,HillshadeVisible=true,HillshadeOpacity=.2};
            var config=JsonSerializer.SerializeToElement(service.Configure(options));
            var contourSignature=config.GetProperty("signature").GetString();
            var shadeSignature=config.GetProperty("hillshadeSignature").GetString();
            const int zoom=15;
            var x=(int)((113.32+180)/360*(1<<zoom));
            var y=(int)((1-Math.Asinh(Math.Tan(24.42*Math.PI/180))/Math.PI)/2*(1<<zoom));
            Uri UriFor(string signature,int xx,int yy)=>new($"https://hikingguide-terrain.local/{signature}/hillshade/{zoom}/{xx}/{yy}.png");
            static (int Clear,int Shadow,int NonBlack,int Maximum) Pixels(byte[] bytes)
            {
                using var bitmap=SKBitmap.Decode(bytes);var clear=0;var shadow=0;var colored=0;var maximum=0;
                for(var py=0;py<bitmap.Height;py++)for(var px=0;px<bitmap.Width;px++)
                {
                    var pixel=bitmap.GetPixel(px,py);if(pixel.Alpha==0)clear++;else shadow++;
                    if(pixel.Red!=0||pixel.Green!=0||pixel.Blue!=0)colored++;
                    maximum=Math.Max(maximum,pixel.Alpha);
                }
                return(clear,shadow,colored,maximum);
            }
            // 真实DEM的旧版灰度结果仅作为对照与旧缓存夹具，绝不用于正常地图路径。
            var legacy=await Task.Run(()=>LegacyShade(database.Id,zoom,x,y));
            await File.WriteAllBytesAsync(Path.Combine(folder,"真实DEM-旧灰度阴影.png"),legacy);
            var oldKey=$"{contourSignature}/hillshade/{zoom}/{x}/{y}.png";
            await MapTileDiskCache.Shared(DemTerrainTiles.CacheRoot).GetAsync(DemTerrainTiles.HillshadeCacheId,oldKey,1024L*1024*1024,
                ()=>Task.FromResult(new CachedMapTile(legacy,"image/png",DateTimeOffset.MaxValue)));
            var png=await service.GetAsync(UriFor(contourSignature!,x,y));
            var pixels=Pixels(png);
            checks["真实DEM阴影同时具有透明受光面与可见暗部"]=pixels.Clear>100&&pixels.Shadow>100;
            checks["新版PNG只含黑色透明阴影没有灰白底罩"]=pixels.NonBlack==0;
            checks["旧签名请求也不会回落到旧灰度缓存"]=pixels.NonBlack==0&&service.GeneratedTiles==1;
            await File.WriteAllBytesAsync(Path.Combine(folder,"真实DEM-新透明阴影.png"),png);
            var repeated=await service.GetAsync(UriFor(shadeSignature!,x,y));
            checks["新版规范阴影缓存跨请求复用"]=service.GeneratedTiles==1&&png.AsEnumerable().SequenceEqual(repeated);
            var changed=JsonSerializer.SerializeToElement(service.Configure(options with{HillshadeOpacity=.8,ContourColor="#FF0000",MajorContourIntervalMeters=100}));
            checks["强度颜色及主等高线间隔不重算阴影瓦片"]=changed.GetProperty("hillshadeSignature").GetString()==shadeSignature;
            service.Configure(options);
            var empty=await service.GetAsync(UriFor(shadeSignature!,0,0));
            checks["DEM无覆盖区域完全透明"]=Pixels(empty).Clear==512*512;
            var flatRoot=Path.Combine(folder,"SyntheticFlatDEM");Directory.CreateDirectory(flatRoot);
            await File.WriteAllBytesAsync(Path.Combine(flatRoot,"N00E000.hgt"),new byte[1201*1201*2]);
            var flat=await DemCatalog.MountAsync("平坦DEM透明验证",flatRoot,unitMetersConfirmed:true);
            var flatConfig=JsonSerializer.SerializeToElement(service.Configure(options with{DemDatabaseId=flat.Database.Id}));
            var tile=HikingGuide.Core.Maps.MapCoordinateTransform.ToTile(.5,.5,zoom);
            var flatPng=await service.GetAsync(UriFor(flatConfig.GetProperty("hillshadeSignature").GetString()!,(int)tile.X,(int)tile.Y));
            checks["平坦真实HGT生成的整张阴影完全透明"]=Pixels(flatPng).Clear==512*512;
            MakeComparison(legacy,png,Path.Combine(folder,"20%叠加强度-同底色对比.png"));
            GlobalSettingsService.Current.OfflineTerrain=options;
            GlobalSettingsService.Current.Terrain3D=new(){Enabled=false,TerrainDemDatabaseId=database.Id,Exaggeration=1.5};
            await GlobalSettingsService.SaveAsync();
            for(var i=0;i<200&&!main.Map.IsReady;i++)await Task.Delay(100);
            if(!main.Map.IsReady)throw new InvalidOperationException("地图未就绪。");
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> Script(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            await main.Map.ApplyGlobalMapLayersAsync(true);
            await Script("window.hikingGuideTerrainTest.camera(15)");
            JsonElement state=default;
            for(var i=0;i<400;i++)
            {
                await Task.Delay(100);state=await Script("window.hikingGuideTerrainTest.inspect()");
                var layers=state.GetProperty("layers");
                if(layers.EnumerateArray().All(l=>l.ValueKind!=JsonValueKind.Null&&l.GetProperty("loaded").GetBoolean()))break;
            }
            var layer=state.GetProperty("layers")[0];
            checks["地图实际加载新版阴影并保留20%叠加强度"]=layer.ValueKind!=JsonValueKind.Null&&
                layer.GetProperty("loaded").GetBoolean()&&Math.Abs(layer.GetProperty("opacity").GetDouble()-.2)<1e-8;
            await CaptureT10ClientAsync(window,Path.Combine(folder,"T181-透明阴影-完整界面.png"));
            await using(var capture=File.Create(Path.Combine(folder,"T181-透明阴影-地图.png")))
                await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,capture);
            var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};
            manager.Show();manager.LibraryTabs.SelectedIndex=1;await T10ManualLayoutAsync(manager);
            checks["阴影控件仍回显用户强度并解释亮部透明"]=manager.TerrainOpacity.Value==20&&manager.TerrainOpacity.ToolTip?.ToString()?.Contains("不覆盖底图",StringComparison.Ordinal)==true;
            await CaptureT10ClientAsync(manager,Path.Combine(folder,"T181-等高线生成器.png"));manager.Close();
            await Script("window.hikingGuide3DTest.at([113.32,24.42],16)");
            GlobalSettingsService.Current.Terrain3D=new(){Enabled=true,TerrainDemDatabaseId=database.Id,Exaggeration=1.5,DefaultPitch=55,ShowContours=true,ShowHillshade=true};
            await GlobalSettingsService.SaveAsync();
            await main.Map.ApplyGlobalMapLayersAsync(true);
            await Script("window.hikingGuide3DTest.camera(55,30)");
            JsonElement terrain=default;
            for(var i=0;i<900;i++)
            {
                await Task.Delay(100);terrain=await Script("window.hikingGuide3DTest.inspect()");
                state=await Script("window.hikingGuideTerrainTest.inspect()");
                if(terrain.GetProperty("effective").GetBoolean()&&state.GetProperty("layers").EnumerateArray().All(l=>l.ValueKind!=JsonValueKind.Null&&l.GetProperty("loaded").GetBoolean()))break;
            }
            checks["3D地形贴合时同样加载透明阴影"]=terrain.GetProperty("effective").GetBoolean()&&state.GetProperty("layers")[0].GetProperty("loaded").GetBoolean();
            checks["3D保持20%阴影强度与1点5地形夸张"]=Math.Abs(state.GetProperty("layers")[0].GetProperty("opacity").GetDouble()-.2)<1e-8&&
                Math.Abs(terrain.GetProperty("terrain").GetProperty("exaggeration").GetDouble()-1.5)<1e-8;
            await using(var capture3D=File.Create(Path.Combine(folder,"T181-透明阴影-3D地图.png")))
                await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,capture3D);
            metrics=new{Database=database.Name,Zoom=zoom,TileX=x,TileY=y,pixels.Clear,pixels.Shadow,pixels.Maximum,Style=HillshadeOverlay.AlgorithmVersion,ShaderSignature=shadeSignature,Terrain=terrain,Status=main.Map.MapStatusText};
        }
        catch(Exception exception){error=exception.ToString();}
        var passed=error is null&&checks.Count>0&&checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Version=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion,Checks=checks,Metrics=metrics,Error=error},
            new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
        return passed;
    }

    private byte[] LegacyShade(Guid database,int zoom,int x,int y)
    {
        const int n=TerrainTileMath.GridCells;var grid=new double?[n+3,n+3];
        for(var gy=-1;gy<=n+1;gy++)for(var gx=-1;gx<=n+1;gx++)
        {var p=TerrainTileMath.Coordinate(zoom,x,y,gx,gy);grid[gy+1,gx+1]=DemProvider!.SampleDetailed(database,new(p.Latitude,p.Longitude)).ElevationMeters;}
        using var shade=new SKBitmap(n,n);shade.Erase(SKColors.Transparent);
        for(var gy=0;gy<n;gy++)for(var gx=0;gx<n;gx++)
        {
            var row=gy+1;var col=gx+1;var step=TerrainTileMath.GroundStep(zoom,TerrainTileMath.Coordinate(zoom,x,y,0,gy).Latitude);
            if(grid[row,col] is null||grid[row,col-1] is not {} w||grid[row,col+1] is not {} e||grid[row-1,col] is not {} north||grid[row+1,col] is not {} south)continue;
            var value=(byte)Math.Round(255*TerrainTileMath.Shade(w,e,north,south,step));shade.SetPixel(gx,gy,new(value,value,value));
        }
        using var scaled=new SKBitmap(512,512);using(var canvas=new SKCanvas(scaled)){canvas.Clear(SKColors.Transparent);using var image=SKImage.FromBitmap(shade);canvas.DrawImage(image,new SKRect(0,0,512,512));}
        using var output=SKImage.FromBitmap(scaled);using var data=output.Encode(SKEncodedImageFormat.Png,100);return data.ToArray();
    }

    private static void MakeComparison(byte[] legacy,byte[] current,string path)
    {
        using var output=new SKBitmap(1024,576);using var canvas=new SKCanvas(output);canvas.Clear(SKColors.White);
        using var font=new SKFont(SKTypeface.FromFamilyName("Microsoft YaHei"),22);
        using var text=new SKPaint{Color=SKColors.Black,IsAntialias=true};
        using var green=new SKPaint{Color=new(194,220,179)};
        using var opacity=new SKPaint{Color=SKColors.White.WithAlpha(51)};
        var images=new[]{legacy,current};var titles=new[]{"旧：灰度覆盖（20%）","新：透明阴影（20%）"};
        for(var i=0;i<2;i++)
        {
            canvas.DrawText(titles[i],i*512+18,34,font,text);canvas.DrawRect(new SKRect(i*512,64,(i+1)*512,576),green);
            using var bitmap=SKBitmap.Decode(images[i]);using var image=SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image,new SKRect(i*512,64,(i+1)*512,576),opacity);
        }
        using var result=SKImage.FromBitmap(output);using var png=result.Encode(SKEncodedImageFormat.Png,100);File.WriteAllBytes(path,png.ToArray());
    }
}
