using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HikingGuide.App.Services;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Maps;
using Microsoft.Web.WebView2.Wpf;
using SkiaSharp;

namespace HikingGuide.App;

public partial class App
{
    private async Task RunTerrainSmokeAsync(MainWindow window,MainViewModel main,WebView2 web,string folder,Dictionary<string,bool> checks,List<object> diagnostics)
    {
        if(!folder.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("地形验收必须隔离。");
        var databases=DemCatalog.Databases.Where(d=>d.Status==HikingGuide.Infrastructure.Elevation.DemDatabaseStatus.Ready).Take(2).ToArray();
        if(databases.Length<2||DemProvider is null)throw new InvalidOperationException("真实地形验收需要两套可读DEM。");
        var service=new DemTerrainTiles(DemCatalog,DemProvider,DemTerrainTiles.CacheRoot);
        var options=new OfflineTerrainSettings{DemDatabaseId=databases[0].Id,ContoursVisible=true,HillshadeVisible=true};
        static (int X,int Y) Tile(int z)=>((int)((113.32+180)/360*(1<<z)),(int)((1-Math.Asinh(Math.Tan(24.42*Math.PI/180))/Math.PI)/2*(1<<z)));
        static int Visible(byte[] bytes)
        {using var bitmap=SKBitmap.Decode(bytes);return Enumerable.Range(0,bitmap.Height).Sum(y=>Enumerable.Range(0,bitmap.Width).Count(x=>bitmap.GetPixel(x,y).Alpha>0));}
        var payload=JsonSerializer.SerializeToElement(service.Configure(options));var signature=payload.GetProperty("signature").GetString();
        Uri UriFor(string kind,int z,int? xx=null,int? yy=null){var (x,y)=Tile(z);return new($"https://hikingguide-terrain.local/{signature}/{kind}/{z}/{xx??x}/{yy??y}.png");}
        var watch=System.Diagnostics.Stopwatch.StartNew();
        foreach(var z in new[]{13,15})
        {
            var png=await service.GetAsync(UriFor("contours",z));await File.WriteAllBytesAsync(Path.Combine(folder,$"DEM真实等高线-Z{z}.png"),png);
            checks[$"真实DEM-Z{z}透明PNG含可见等高线"]=Visible(png)>100;
            var count=service.GeneratedTiles;var repeat=await service.GetAsync(UriFor("contours",z));
            checks[$"真实DEM-Z{z}缓存复用不重复计算"]=count==service.GeneratedTiles&&png.SequenceEqual(repeat);
        }
        var shade=await service.GetAsync(UriFor("hillshade",15));await File.WriteAllBytesAsync(Path.Combine(folder,"DEM真实阴影.png"),shade);
        checks["真实DEM阴影仅覆盖背光部分且亮部透明"]=Visible(shade)>100&&Visible(shade)<512*512;
        var thinConfig=JsonSerializer.SerializeToElement(service.Configure(options with{MajorContoursEnabled=false}));signature=thinConfig.GetProperty("signature").GetString();
        var thin=await service.GetAsync(UriFor("contours",15));
        var majorConfig=JsonSerializer.SerializeToElement(service.Configure(options));signature=majorConfig.GetProperty("signature").GetString();
        var bold=await service.GetAsync(UriFor("contours",15));await File.WriteAllBytesAsync(Path.Combine(folder,"DEM主等高线50米.png"),bold);
        checks["主等高线实际PNG加粗增加线条像素"]=Visible(bold)>Visible(thin);
        checks["主等高线规则进入等高线缓存身份"]=majorConfig.GetProperty("signature").GetString()!=thinConfig.GetProperty("signature").GetString();
        var major100=JsonSerializer.SerializeToElement(service.Configure(options with{MajorContourIntervalMeters=100}));
        checks["更改主等高线间隔不更改阴影缓存身份"]=major100.GetProperty("hillshadeSignature").GetString()==majorConfig.GetProperty("hillshadeSignature").GetString()&&major100.GetProperty("signature").GetString()!=majorConfig.GetProperty("signature").GetString();
        signature=JsonSerializer.SerializeToElement(service.Configure(options)).GetProperty("signature").GetString();
        static Dictionary<string,string> DiskHashes(Guid id)
        {
            var root=Path.Combine(DemTerrainTiles.CacheRoot,id.ToString("N"));
            return Directory.Exists(root)?Directory.GetFiles(root,"*.tile").ToDictionary(p=>p,p=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))):[];
        }
        var shadeHashes=DiskHashes(DemTerrainTiles.HillshadeCacheId);var generatedBeforeClear=service.GeneratedTiles;
        await service.ClearCacheAsync(true);
        checks["清除等高线缓存包含磁盘且不清阴影"]=DiskHashes(DemTerrainTiles.ContourCacheId).Count==0&&shadeHashes.Count==DiskHashes(DemTerrainTiles.HillshadeCacheId).Count&&shadeHashes.All(p=>DiskHashes(DemTerrainTiles.HillshadeCacheId).GetValueOrDefault(p.Key)==p.Value);
        await service.GetAsync(UriFor("contours",15));checks["清除后未复用旧内存等高线并可重新生成"]=service.GeneratedTiles>generatedBeforeClear;
        var contourHashes=DiskHashes(DemTerrainTiles.ContourCacheId);generatedBeforeClear=service.GeneratedTiles;
        await service.ClearCacheAsync(false);
        checks["清除阴影缓存不清等高线"]=DiskHashes(DemTerrainTiles.HillshadeCacheId).Count==0&&contourHashes.All(p=>DiskHashes(DemTerrainTiles.ContourCacheId).GetValueOrDefault(p.Key)==p.Value);
        await service.GetAsync(UriFor("hillshade",15));checks["阴影清除后可重新生成"]=service.GeneratedTiles>generatedBeforeClear;
        var empty=await service.GetAsync(UriFor("contours",15,0,0));checks["无覆盖区域透明不生成海平面"]=Visible(empty)==0;
        var restarted=new DemTerrainTiles(DemCatalog,DemProvider,DemTerrainTiles.CacheRoot);restarted.Configure(options);
        await restarted.GetAsync(UriFor("contours",15));checks["地形缓存重启后复用"]=restarted.GeneratedTiles==0;
        var switched=JsonSerializer.SerializeToElement(service.Configure(options with {DemDatabaseId=databases[1].Id}));
        checks["切换DEM缓存键隔离"]=signature!=switched.GetProperty("signature").GetString();signature=switched.GetProperty("signature").GetString();
        var second=await service.GetAsync(UriFor("contours",15));checks["第二套真实DEM成功生成等高线"]=Visible(second)>100;
        var changed=JsonSerializer.SerializeToElement(service.Configure(options with {ContourColor="#AA1122"}));
        checks["切换颜色缓存键隔离"]=changed.GetProperty("signature").GetString()!=payload.GetProperty("signature").GetString();
        signature=changed.GetProperty("signature").GetString();
        service.SetViewport(0,0,1,1);
        try {await service.GetAsync(UriFor("contours",16));checks["离屏生成自动跳过"]=false;}
        catch(OperationCanceledException){checks["离屏生成自动跳过"]=true;}
        service.SetViewport(113.3,24.4,113.4,24.5);
        checks["返回视野后取消瓦片可以重新生成"]=Visible(await service.GetAsync(UriFor("contours",16)))>100;
        var missing=JsonSerializer.SerializeToElement(service.Configure(options with{DemDatabaseId=Guid.NewGuid()}));
        checks["失效DEM引用禁用派生层而不崩溃"]=!missing.GetProperty("contours").GetBoolean()&&!missing.GetProperty("hillshade").GetBoolean();
        var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};manager.Show();
        var tabs=FavoritesPanel.Descendants<TabControl>(manager).Single();tabs.SelectedIndex=1;
        checks["地图管理具有等高线生成器标签"]=tabs.Items.OfType<TabItem>().Any(t=>t.Header?.ToString()=="等高线生成器");
        checks["离线缓存默认2GB与1GB"]=manager.TerrainContourCache.Text=="2048"&&manager.TerrainShadeCache.Text=="1024";
        checks["主等高线默认启用50米倍数"]=manager.TerrainMajorContours.IsChecked==true&&manager.TerrainMajorInterval.Text=="50";
        checks["等高线与阴影各有清除按钮"]=manager.ClearContoursButton.Content?.ToString()=="清除高程采样缓存"&&manager.ClearShadeButton.Content?.ToString()=="清除阴影缓存";
        manager.TerrainDem.SelectedValue=options.DemDatabaseId;manager.TerrainContours.IsChecked=true;manager.TerrainHillshade.IsChecked=true;
        await T10ManualLayoutAsync(manager);await CaptureT10ClientAsync(manager,Path.Combine(folder,"离线地图库.png"));
        var about=new AboutWindow{Owner=manager};about.Show();await T10ManualLayoutAsync(about);
        checks["关于窗口与程序集版本一致"]=about.VersionLabel.Text=="软件版本："+HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion;
        await CaptureT10ClientAsync(about,Path.Combine(folder,"关于-V1.6.0.png"));about.Close();
        manager.TerrainContourCache.Text="12";manager.TerrainShadeCache.Text="7";
        options=options with{ContourCacheMb=12,HillshadeCacheMb=7};
        FavoritesPanel.Descendants<Button>(manager).Single(b=>b.Content?.ToString()=="应用并保存等高线生成设置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(700);manager.Close();
        checks["离线图层设置保存并通知地图"]=GlobalSettingsService.Current.OfflineTerrain==options;
        await GlobalSettingsService.LoadAsync();
        checks["自定义离线缓存容量重新读取保持"]=GlobalSettingsService.Current.OfflineTerrain==options;
        async Task<JsonElement> Script(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
        await main.Map.ApplyGlobalMapLayersAsync(true);await Task.Delay(300);
        var state=await Script("window.hikingGuideTerrainTest.inspect()");
        checks["原生地图装载三组派生层"]=state.GetProperty("layers").EnumerateArray().Count(l=>l.ValueKind!=JsonValueKind.Null)==3;
        var layer=state.GetProperty("layers")[1];checks["100米层严格在Z15前截止"]=layer.GetProperty("min").GetInt32()==13&&layer.GetProperty("max").GetInt32()==15;
        await Script("window.hikingGuideTerrainTest.camera(15)");
        for(var i=0;i<300;i++)
        {
            await Task.Delay(100);state=await Script("window.hikingGuideTerrainTest.inspect()");
            if(state.GetProperty("layers").EnumerateArray().All(l=>l.GetProperty("loaded").GetBoolean()))break;
        }
        checks["原生地图真实DEM按视野加载完成"]=state.GetProperty("layers").EnumerateArray().All(l=>l.GetProperty("loaded").GetBoolean());
        await CaptureT10ClientAsync(window,Path.Combine(folder,"等高线及阴影地图-Z15.png"));
        diagnostics.Add(new{TerrainDatabases=databases.Select(d=>d.Name).ToArray(),TerrainSeconds=watch.Elapsed.TotalSeconds,TerrainGenerated=service.GeneratedTiles});
    }
}
