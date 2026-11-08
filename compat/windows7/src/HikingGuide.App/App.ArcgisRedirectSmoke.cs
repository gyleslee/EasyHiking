using System.IO;
using System.Text.Json;
using HikingGuide.App.Services;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Infrastructure.Projects;
using Microsoft.Web.WebView2.Wpf;

namespace HikingGuide.App;

public partial class App
{
    private async Task<bool> RunArcgisRedirectSmokeAsync(MainWindow window, MainViewModel main, string output)
    {
        var checks = new Dictionary<string, bool>(); string? error = null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        try
        {
            if (!folder.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("地图验证输出必须使用隔离目录。");
            Directory.CreateDirectory(folder);
            var source = new MapSourceDefinition { Name = "Esri世界卫星地图（HTTP跳转验证）",
                UrlTemplate = "http://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
                MinZoom = 0, MaxZoom = 19, CoordinateSystem = MapSourceCoordinateSystem.Wgs84 };
            GlobalSettingsService.Current.MapSources.Clear(); GlobalSettingsService.Current.MapSources.Add(source);
            GlobalSettingsService.Current.MapLayerStack = new() { BaseSourceId = source.Id };
            await GlobalSettingsService.SaveAsync();
            checks["原始HTTP图源测试通过"] = (await MapSourceTestService.TestAsync(source)).IsSuccess;
            var project = new HikingProject { Name = "ArcGIS重定向隔离验证" };
            project.Settings.Map.Center = new(23.13, 113.26); project.Settings.Map.Zoom = 13;
            var projectPath = Path.Combine(folder, "地图验证.hikeproj");
            await new ProjectFileService().SaveAsync(project, projectPath, true);
            await main.OpenForAutomationAsync(projectPath);
            for (var i = 0; i < 200 && !main.Map.IsReady; i++) await Task.Delay(100);
            await main.WaitForMapRefreshForAutomationAsync(); await main.Map.ApplyGlobalMapLayersAsync(true);
            var web = FavoritesPanel.Descendants<WebView2>(window).First(w => w.Source?.Host == "hikingguide.local");
            async Task<JsonElement> JS(string code) => JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            var loaded = false;
            for (var i = 0; i < 450; i++)
            {
                var state = await JS("window.hikingGuideRasterTest.inspect()");
                if (state.GetProperty("slots")[0].GetProperty("loaded").GetBoolean()) { loaded = true; break; }
                await Task.Delay(100);
            }
            checks["真实WebView地图底图加载完成"] = loaded;
            await main.Map.CapturePreviewAsync(Path.Combine(folder, "Esri卫星地图-修复后.png"));
            var bridge = T10ManualField<WebViewMapBridge>(FavoritesPanel.Descendants<MapView>(window).Single(), "_bridge");
            var slots = bridge.ConfigureRasterLayers(MapLayerStackService.Build(GlobalSettingsService.Current.MapLayerStack!, GlobalSettingsService.Current.MapSources));
            var uri = MapTileTemplateParser.BuildTileUrl(slots[0].Templates[0], 13, 6671, 3530);
            await JS("window.arcgisResult=null;fetch(" + JsonSerializer.Serialize(uri)
                + ").then(async r=>{const b=await r.arrayBuffer();window.arcgisResult={status:r.status,type:r.headers.get('Content-Type'),bytes:b.byteLength,magic:Array.from(new Uint8Array(b).slice(0,3))};}).catch(e=>window.arcgisResult={error:e.message})");
            JsonElement result = default;
            for (var i = 0; i < 300; i++) { result = await JS("window.arcgisResult"); if (result.ValueKind == JsonValueKind.Object) break; await Task.Delay(100); }
            checks["浏览器实际收到JPEG瓦片而非301网页"] = result.TryGetProperty("status", out var status) && status.GetInt32() == 200
                && result.GetProperty("type").GetString() == "image/jpeg" && result.GetProperty("bytes").GetInt32() > 1000
                && result.GetProperty("magic")[0].GetInt32() == 255 && result.GetProperty("magic")[1].GetInt32() == 216;
            checks["HTTP图源缓存正常生成"] = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(GlobalSettingsService.SettingsFilePath)!, "MapTileCache"), "*", SearchOption.AllDirectories).Length > 0;
            checks["保存配置仍保留用户导入的原地址"] = source.UrlTemplate.StartsWith("http://server.arcgisonline.com/", StringComparison.Ordinal);
        }
        catch (Exception exception) { error = exception.ToString(); }
        var passed = error is null && checks.Values.All(v => v);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Passed = passed, Checks = checks, Error = error }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }
}
