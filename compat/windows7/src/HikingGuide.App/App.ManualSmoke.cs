using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunManualSmokeAsync(MainWindow window,string output)
    {
        var checks=new Dictionary<string,bool>();string? error=null;
        try
        {
            var help=FavoritesPanel.Descendants<Menu>(window).SelectMany(m=>m.Items.OfType<MenuItem>()).Single(m=>Equals(m.Header,"帮助"));
            checks["帮助菜单显示操作说明"]=help.Items.OfType<MenuItem>().Any(m=>Equals(m.Header,"操作说明"))&&!help.Items.OfType<MenuItem>().Any(m=>m.Header?.ToString()?.Contains("草稿")==true);
            var about=new AboutWindow{Owner=window};about.Show();await T10ManualLayoutAsync(about);
            checks["关于窗口显示操作说明"]=FavoritesPanel.Descendants<Button>(about).Any(b=>Equals(b.Content,"操作说明"));about.Close();
            var folder=Path.GetDirectoryName(output)!;var docs=Path.Combine(AppContext.BaseDirectory,"docs");
            var main=(HikingGuide.App.ViewModels.MainViewModel)window.DataContext;
            var dem=DemCatalog.Databases.FirstOrDefault(d=>d.Status==HikingGuide.Infrastructure.Elevation.DemDatabaseStatus.Ready&&d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));
            if(dem is not null)
            {
                GlobalSettingsService.Current.Terrain3D=(GlobalSettingsService.Current.Terrain3D??new HikingGuide.Core.Models.Terrain3DSettings()) with{TerrainDemDatabaseId=dem.Id};
                GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{DemDatabaseId=dem.Id,ContoursVisible=true,HillshadeVisible=true};
                var terrain=new Terrain3DWindow(main.Map){Owner=window};terrain.Show();
                for(var i=0;i<160&&T10ManualField<bool>(terrain,"_loading");i++)await Task.Delay(250);
                checks["3D设置完成DEM状态读取"]=!T10ManualField<bool>(terrain,"_loading")&&terrain.DemBox.SelectedItem is not null;
                await T10ManualLayoutAsync(terrain);await CaptureT10ClientAsync(terrain,Path.Combine(folder,"85-terrain-settings.png"));terrain.Close();
            }
            for(var i=0;i<160&&!main.Map.IsReady;i++)await Task.Delay(100);
            var mapWeb=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            await mapWeb.CoreWebView2.ExecuteScriptAsync("window.hikingGuide3DTest.at([113.32,24.42],14)");
            using(var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var selection=main.Map.SelectOfflineRegionAsync(cancel.Token);
                for(var i=0;i<100;i++){if(await mapWeb.CoreWebView2.ExecuteScriptAsync("window.hikingGuideOfflineTest.inspect().active")=="true")break;await Task.Delay(100);}
                foreach(var p in new[]{(113.315,24.417),(113.325,24.417),(113.325,24.423),(113.315,24.423)})await mapWeb.CoreWebView2.ExecuteScriptAsync(FormattableString.Invariant($"window.hikingGuideOfflineTest.click({p.Item1},{p.Item2})"));
                await Task.Delay(2000);
                await using(var image=File.Create(Path.Combine(folder,"89-offline-region.png")))await mapWeb.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,image);
                await mapWeb.CoreWebView2.ExecuteScriptAsync("window.hikingGuideOfflineTest.finish()");
                checks["真实地图四点多边形选区"]=await selection is {Points.Count:4};await main.Map.ClearOfflineRegionAsync();
            }
            checks["程序目录说明可编辑"]=File.Exists(Path.Combine(docs,"易行徒步规划制作器-操作说明.md"))&&!File.GetAttributes(Path.Combine(docs,"易行徒步规划制作器-操作说明.md")).HasFlag(FileAttributes.ReadOnly);
            var web=new WebView2();var reader=new Window{Title="操作说明验证",Content=web,Width=1280,Height=800,Owner=window};reader.Show();
            await web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null,Path.Combine(folder,"ManualWebView2")));
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("hikingguide-manual.local",docs,CoreWebView2HostResourceAccessKind.Allow);
            var ready=new TaskCompletionSource<bool>();web.NavigationCompleted+=(_,e)=>ready.TrySetResult(e.IsSuccess);
            web.Source=new Uri("https://hikingguide-manual.local/"+Uri.EscapeDataString("易行徒步规划制作器-操作说明.html"));
            if(!await ready.Task.WaitAsync(TimeSpan.FromSeconds(20)))throw new InvalidOperationException("说明页加载失败");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('img').loading='eager'");
            await Task.Delay(800);
            var json=JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("JSON.stringify({title:document.title,brand:document.querySelector('header .brand').textContent,h1:document.querySelector('h1').textContent,draft:document.body.innerText.includes('草稿'),sections:document.querySelectorAll('h2').length,firstImage:document.querySelector('img').complete&&document.querySelector('img').naturalWidth>0})"));
            var page=JsonSerializer.Deserialize<JsonElement>(json!);
            checks["说明标题页头正文统一且无版本号"]=new[]{"title","brand","h1"}.All(p=>page.GetProperty(p).GetString()=="易行徒步规划制作器 操作说明");
            checks["说明不含草稿标记"]=!page.GetProperty("draft").GetBoolean();
            checks["20节使用说明及截图可阅读"]=page.GetProperty("sections").GetInt32()==21&&page.GetProperty("firstImage").GetBoolean();
            await using(var shot=File.Create(Path.Combine(folder,"操作说明.png")))await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,shot);
            reader.Close();web.Dispose();
        }
        catch(Exception e){error=e.ToString();}
        var passed=error is null&&checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));return passed;
    }
}



