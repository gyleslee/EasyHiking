using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
using Microsoft.Web.WebView2.Wpf;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunElevationLinkSmokeAsync(MainWindow window, MainViewModel main, string output)
    {
        var checks = new Dictionary<string, bool>(); string? error = null;
        FavoritesPanel.MouseProbe.GetCursorPos(out var cursor); var foreground = FavoritesPanel.MouseProbe.GetForegroundWindow();
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
            if (!Favorites.Paths.FavoritesRoot.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase) || !folder.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("曲线联动验收必须使用隔离数据。");
            Directory.CreateDirectory(folder);
            var track = new Track { Name = "高程曲线联动验收", Segments = [new() { Points = Enumerable.Range(0, 51).Select(i => new TrackPoint { Latitude = 23.1 + i * .0002, Longitude = 113.3 + i * .0002, SourceElevation = 100 + Math.Sin(i * .2) * 60 + i * 2 }).ToList() }] };
            track.RecalculateDistances(); var project = new HikingProject { Name = "曲线联动隔离验收" }; project.Tracks.Add(track); project.MainGuideTrackId = track.Id;
            foreach (var i in new[] {5,15,25,35,45})
            {
                var point = track.Segments[0].Points[i];
                var node = new GuideNode { Name = "不在曲线显示的名称" + i, TrackId = track.Id, OriginalCoordinate = point.Coordinate };
                new HikingGuide.Application.Guides.GuideStructureService().SnapNode(node,track); project.GuideNodes.Add(node);
            }
            HikingGuide.Application.Guides.GuideStructureService.ReplaceSegments(project,new HikingGuide.Application.Guides.GuideStructureService().RebuildSegments(project));
            var path = Path.Combine(folder,"曲线联动.hikeproj"); await new ProjectFileService().SaveAsync(project,path,true); await main.OpenForAutomationAsync(path);
            project = main.MapContextProject!; track = project.Tracks[0];
            window.Width = 1500; window.Height = 1000; window.BottomPanelRow.Height = new GridLength(350); window.Topmost = true; window.Activate(); FavoritesPanel.MouseProbe.SetForegroundWindow(new WindowInteropHelper(window).Handle);
            for (var i=0;i<150 && !main.Map.IsReady;i++) await Task.Delay(100);
            await Task.Delay(600);
            checks["GPS曲线完成分析"] = await window.RunP8GpsUiSmokeAsync(project,track); await Task.Delay(400);
            var web = FavoritesPanel.Descendants<WebView2>(window).First(w => w.Source?.Host == "hikingguide.local");
            async Task<JsonElement> State() => JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync("window.hikingGuideFavoriteTest.inspect()"));
            bool Located(JsonElement state, HikingGuide.Core.Geometry.GeoCoordinate coordinate) => Math.Abs(state.GetProperty("lng").GetDouble()-coordinate.Longitude)<.00001 && Math.Abs(state.GetProperty("lat").GetDouble()-coordinate.Latitude)<.00001;
            var before = await State(); var zoom = before.GetProperty("zoom").GetDouble();
            checks["黄色圆点大小合适且不含名称"] = window.ElevationAnalysis.GuideDotsProbe(5);
            foreach (var node in new[] {false,true})
            {
                var probe = window.ElevationAnalysis.LinkProbe(node); FavoritesPanel.MouseProbe.SetCursorPos((int)probe.Screen.X,(int)probe.Screen.Y); await Task.Delay(100);
                FavoritesPanel.MouseProbe.mouse_event(2,0,0,0,UIntPtr.Zero); await Task.Delay(70); FavoritesPanel.MouseProbe.mouse_event(4,0,0,0,UIntPtr.Zero); await Task.Delay(700);
                var after = await State(); checks[node ? "点击黄色节点定位吸附坐标" : "点击曲线点实际移动地图"] = Located(after,probe.Coordinate);
                checks["定位保持地图层级:"+node] = Math.Abs(after.GetProperty("zoom").GetDouble()-zoom)<.001;
            }
            var stable = await State(); var drag = window.ElevationAnalysis.LinkProbe(false).Screen;
            FavoritesPanel.MouseProbe.SetCursorPos((int)drag.X,(int)drag.Y); FavoritesPanel.MouseProbe.mouse_event(2,0,0,0,UIntPtr.Zero); await Task.Delay(50);
            for(var i=1;i<=5;i++) { FavoritesPanel.MouseProbe.SetCursorPos((int)drag.X+i*10,(int)drag.Y); await Task.Delay(50); }
            FavoritesPanel.MouseProbe.mouse_event(4,0,0,0,UIntPtr.Zero); await Task.Delay(400);
            var final = await State(); checks["拖动曲线不会误触发地图定位"] = Math.Abs(final.GetProperty("lng").GetDouble()-stable.GetProperty("lng").GetDouble())<1e-8 && Math.Abs(final.GetProperty("lat").GetDouble()-stable.GetProperty("lat").GetDouble())<1e-8;
            checks["原始轨迹和攻略节点保留"] = track.GetPointCount()==51 && project.GuideNodes.Count==5;
            CaptureWindowPng(window,Path.ChangeExtension(output,".png"));
        }
        catch(Exception ex) { error=ex.ToString(); }
        finally { FavoritesPanel.MouseProbe.mouse_event(4,0,0,0,UIntPtr.Zero); FavoritesPanel.MouseProbe.SetCursorPos(cursor.X,cursor.Y); if(foreground!=IntPtr.Zero) FavoritesPanel.MouseProbe.SetForegroundWindow(foreground); }
        var success=error is null && checks.Count>0 && checks.Values.All(v=>v); await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Success=success,Checks=checks,Error=error},new JsonSerializerOptions{WriteIndented=true})); return success;
    }
}
