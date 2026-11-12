using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using HikingGuide.App.ViewModels;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.Routing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HikingGuide.App;

public partial class App
{
    private async Task<bool> RunInstallationSmokeAsync(MainWindow window,MapViewModel map,string output)
    {
        var folder=Path.GetDirectoryName(Path.GetFullPath(output))!;
        var checks=new Dictionary<string,bool>();string? error=null;
        if(!folder.StartsWith(@"F:\Codex\Temp\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("安装验收必须隔离");
        var app=AppContext.BaseDirectory;
        Dictionary<string,string> Snapshot()=>Directory.GetFiles(app,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var before=Snapshot();
        try
        {
            var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var expectedRoot=ApplicationDataPaths.UsesInstallationDrive(app)?ApplicationDataPaths.InstallationDriveUserRoot(app,local):Path.Combine(local,"EasyHiking");
            checks["安装版默认用户数据位置正确"]=ApplicationDataPaths.DefaultRootDirectory(app)==Path.Combine(expectedRoot,"Data");
            checks["安装标识已随程序部署"]=ApplicationDataPaths.IsInstalledApplication(app);
            var defaultRouter=new BRouterPathProvider();
            checks["路由数据与程序运行库分离"]=defaultRouter.RuntimeDirectory==Path.Combine(app,"BRouter","runtime")&&defaultRouter.BRouterRoot!=Path.Combine(app,"BRouter");
            checks["新配置等高线和30%阴影默认启用"]=GlobalSettingsService.Current.OfflineTerrain is{ContoursVisible:true,HillshadeVisible:true,HillshadeOpacity:.3};
            checks["首次设置状态初始未完成"]=!GlobalSettingsService.Current.FirstRunSetupCompleted;
            var seed=JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(app,"Defaults","settings.json")));
            checks["T180程序集与窗口版本一致"]=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion=="V1.9.6"&&window.Title.Contains("V1.9.6",StringComparison.Ordinal)
                &&System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(app,"易行徒步规划制作器.dll")).FileVersion=="1.9.6.0";
            checks["新配置地形夸张默认1.5且不携带个人DEM"]=GlobalSettingsService.Current.Terrain3D is{Exaggeration:1.5,TerrainDemDatabaseId:null,Enabled:false};
            var layout=GlobalSettingsService.Current.PanelLayout!;
            checks["预置布局与用户指定F盘布局一致"]=Math.Abs(layout.BottomFraction-seed.GetProperty("panelLayout").GetProperty("bottomFraction").GetDouble())<.000001
                &&Math.Abs(layout.ProjectFraction-seed.GetProperty("panelLayout").GetProperty("projectFraction").GetDouble())<.000001
                &&Math.Abs(layout.PropertiesFraction-seed.GetProperty("panelLayout").GetProperty("propertiesFraction").GetDouble())<.000001;
            await T10ManualLayoutAsync(window);
            var actual=window.CapturePanelLayout()!;
            checks["真实界面按默认上下与左右比例显示"]=Math.Abs(actual.BottomFraction-layout.BottomFraction)<.005
                &&Math.Abs(actual.ProjectFraction-layout.ProjectFraction)<.005&&Math.Abs(actual.PropertiesFraction-layout.PropertiesFraction)<.005;
            checks["预置攻略与每日行程列宽读取正确"]=seed.GetProperty("guideTableColumnWidths").EnumerateObject().All(p=>GlobalSettingsService.Current.GuideTableColumnWidths.GetValueOrDefault(p.Name)==p.Value.GetDouble());
            checks["当前全部11个图源读入且原配置完整"]=GlobalSettingsService.Current.MapSources.Count==11
                &&seed.GetProperty("mapSources").EnumerateArray().All(s=>GlobalSettingsService.Current.MapSources.Any(m=>m.Id.ToString()==s.GetProperty("id").GetString()&&m.UrlTemplate==s.GetProperty("urlTemplate").GetString()));
            for(var i=0;i<200&&!map.IsReady;i++)await Task.Delay(100);
            if(!map.IsReady)throw new InvalidOperationException("地图尚未就绪，停止默认布局截图。");
            await CaptureT10ClientAsync(window,Path.Combine(folder,"T180-默认布局.png"));
            var about=new AboutWindow{Owner=window};about.Show();await T10ManualLayoutAsync(about);
            checks["关于窗口显示T180"]=about.VersionLabel.Text=="软件版本：V1.9.6";
            await CaptureT10ClientAsync(about,Path.Combine(folder,"T180-关于.png"));about.Close();
            var terrain=new Terrain3DWindow(map){Owner=window};terrain.Show();await T10ManualLayoutAsync(terrain);
            checks["3D设置实际控件回显1.5"]=terrain.Exaggeration.Value==1.5&&terrain.ExaggerationInput.Text==1.5.ToString("F1",System.Globalization.CultureInfo.CurrentCulture);
            await CaptureT10ClientAsync(terrain,Path.Combine(folder,"T180-3D默认设置.png"));terrain.Close();
            var manual=new WebView2();var manualWindow=new Window{Title="V1.9.6操作说明验证",Content=manual,Width=1280,Height=800,Owner=window};manualWindow.Show();
            var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(folder,"ManualWebView2"));
            await manual.EnsureCoreWebView2Async(env);
            manual.CoreWebView2.SetVirtualHostNameToFolderMapping("hikingguide-manual.local",Path.Combine(app,"docs"),CoreWebView2HostResourceAccessKind.Allow);
            var ready=new TaskCompletionSource<bool>();manual.NavigationCompleted+=(_,e)=>ready.TrySetResult(e.IsSuccess);
            manual.Source=new Uri("https://hikingguide-manual.local/"+Uri.EscapeDataString("易行徒步规划制作器-操作说明.html"));
            if(!await ready.Task.WaitAsync(TimeSpan.FromSeconds(20)))throw new InvalidOperationException("说明页加载失败");
            var text=JsonSerializer.Deserialize<JsonElement>(await manual.CoreWebView2.ExecuteScriptAsync("JSON.stringify({title:document.title,brand:document.querySelector('header .brand').textContent,h1:document.querySelector('h1').textContent})"));
            var labels=JsonSerializer.Deserialize<JsonElement>(text.GetString()!);
            checks["说明浏览器标题页头和正文使用固定名称"]=new[]{"title","brand","h1"}.All(p=>labels.GetProperty(p).GetString()=="易行徒步规划制作器 操作说明");
            await using(var screenshot=File.Create(Path.Combine(folder,"T180-说明页顶部.png")))
                await manual.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,screenshot);
            manualWindow.Close();manual.Dispose();
            var originalSource=GlobalSettingsService.Current.MapSources.Count;
            var demDir=Path.Combine(folder,"SyntheticDEM");Directory.CreateDirectory(demDir);
            await File.WriteAllBytesAsync(Path.Combine(demDir,"N00E000.hgt"),new byte[1201*1201*2]);
            var mounted=await DemCatalog.MountAsync("向导演示DEM",demDir,unitMetersConfirmed:true);
            using var terrainVerifier=new HikingGuide.App.Services.TerrainTileGenerator(DemCatalog);
            var terrainConfig=terrainVerifier.Configure(new(){Enabled=true,TerrainDemDatabaseId=mounted.Database.Id,Exaggeration=1.5});
            var tilePoint=HikingGuide.Core.Maps.MapCoordinateTransform.ToTile(.5,.5,terrainConfig.MaxNativeZoom);
            var tileUrl=terrainConfig.TerrainUrlTemplate!.Replace("{z}",terrainConfig.MaxNativeZoom.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{x}",((int)tilePoint.X).ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace("{y}",((int)tilePoint.Y).ToString(System.Globalization.CultureInfo.InvariantCulture));
            var image=await Task.Run(()=>terrainVerifier.GetAsync(new Uri(tileUrl)));
            using(var bitmap=SkiaSharp.SKBitmap.Decode(image))
            {
                var pixel=bitmap.GetPixel(256,256);
                checks["真实GDAL采样生成512像素TerrainRGB瓦片"]=bitmap.Width==512&&bitmap.Height==512&&Math.Abs(HikingGuide.Core.Elevation.TerrainRgb.Decode(pixel.Red,pixel.Green,pixel.Blue))<.1;
            }
            terrainVerifier.Configure(new());
            var wizard=new FirstRunSetupWindow(window,map);var titles=new List<string>();
            wizard.StepOpened+=dialog=>dialog.Loaded+=async(_,_)=>
            {
                titles.Add(dialog.Title);await T10ManualLayoutAsync(dialog);
                await CaptureT10ClientAsync(dialog,Path.Combine(folder,$"首次设置-{titles.Count}.png"));dialog.Close();
            };
            wizard.StepsFinished+=async()=>{await T10ManualLayoutAsync(wizard);await CaptureT10ClientAsync(wizard,Path.Combine(folder,"首次设置-向导.png"));await wizard.FinishAsync();};
            wizard.ShowDialog();
            checks["实际依次自动打开DEM和BRouter设置"]=titles.Count==2&&titles[0].Contains("DEM",StringComparison.Ordinal)&&titles[1].Contains("BRouter",StringComparison.Ordinal);
            var reloaded=new GlobalSettingsService(new ApplicationDataPaths(),LoggingService,MapSourceTemplateService);await reloaded.LoadAsync();
            checks["完成状态保存后下次不重复启动向导"]=reloaded.Current.FirstRunSetupCompleted;
            checks["所选DEM与30%阴影设置保存正确"]=reloaded.Current.OfflineTerrain.DemDatabaseId==mounted.Database.Id&&reloaded.Current.OfflineTerrain is{ContoursVisible:true,HillshadeVisible:true,HillshadeOpacity:.3};
            checks["预置图源读取和保存后未丢失"]=reloaded.Current.MapSources.Count==originalSource&&originalSource>0;
            // 使用现有瓦片的只读副本测试安装版Java/配置在只读程序目录下真实规划。
            var router=BRouterController!;Directory.CreateDirectory(router.Data.CurrentSegmentsDirectory);
            File.Copy(@"F:\Hiking Guide\易行徒步规划制作器\BRouter\RoutingData\current\E110_N20.rd5",Path.Combine(router.Data.CurrentSegmentsDirectory,"E110_N20.rd5"));
            var route=await router.Provider.RouteFootShortestAsync(new(23.1382,113.3270),new(23.1386,113.3280));
            checks["Java及只读profiles可用且真实徒步规划成功"]=route.Geometry.Count>=2;
            await router.Process.StopAsync();
        }
        catch(Exception ex){error=ex.ToString();}
        var after=Snapshot();
        checks["运行和向导不在程序目录写入文件"]=before.Count==after.Count&&before.All(p=>after.GetValueOrDefault(p.Key)==p.Value);
        var passed=error is null&&checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,
            Elevated=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            DefaultDataRoot=ApplicationDataPaths.DefaultRootDirectory(app),Error=error},new JsonSerializerOptions{WriteIndented=true}));return passed;
    }
}



