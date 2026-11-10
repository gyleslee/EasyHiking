using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Maps;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Core.Software;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.TrackFiles;
using Microsoft.Web.WebView2.Wpf;
using Track = HikingGuide.Core.Models.Track;

namespace HikingGuide.App;

public partial class App
{
    private async Task<bool> RunT10SmokeAsync(MainWindow window, MainViewModel main, string output, bool manual)
    {
        var checks = new Dictionary<string, bool>(); var diagnostics = new List<object>(); string? error = null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        try
        {
            if (!folder.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase) ||
                !GlobalSettingsService.SettingsFilePath.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase) ||
                !Favorites.Paths.FavoritesRoot.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("T1.0验收与截图必须隔离正式数据。");
            Directory.CreateDirectory(folder);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_VERSION_PREFIX_TEST")=="1")return await RunVersionPrefixSmokeAsync(window,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T195_TEST")=="1")return await RunT195SmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T194_TEST")=="1")return await RunT194SmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T193_TEST")=="1")return await RunTrackSceneSmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_SPLIT_NAMING_TEST")=="1")return await RunSplitNamingSmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T192_TEST")=="1")return await RunT192SmokeAsync(window,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_MANUAL_TEST")=="1")return await RunManualSmokeAsync(window,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_OSM_POLICY_TEST")=="1")return await RunOsmPolicySmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T191_TEST")=="1")return await RunRoadStationarySmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T190_TEST")=="1")return await RunRoadRenderingSmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T189_TEST")=="1")return await RunTerrainRecoverySmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T188_TEST")=="1")return await RunRoadThemeSmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T187_TEST")=="1")return await RunRoadDefaultsSmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T186_TEST")=="1")return await RunMapControlsSmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T185_TEST")=="1")return await RunOfflineMapSmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T184_TEST")=="1")return await RunOsmRoadSmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T183_TEST")=="1")return await RunTerrainPerformanceSmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_T182_TEST")=="1")return await RunTerrainGeneratorSmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_HILLSHADE_TEST")=="1")return await RunHillshadeSmokeAsync(window,main,output);
            if(Environment.GetEnvironmentVariable("HIKINGGUIDE_PRECISION_TEST")=="1")return await RunTerrainPrecisionSmokeAsync(window,main,output);
            var project = new HikingProject { Name = manual ? "山脊徒步示例（说明截图，非实际出行建议）" : "T1.0隔离交互验收" };
            Track Make(string name, double offset, string color) => new()
            {
                Name = name, DisplayColor = color,
                Segments = [new() { Points = Enumerable.Range(0, 101).Select(i => new TrackPoint
                {
                    Latitude = 23.1 + offset + Math.Sin(i * .35) * .00004,
                    Longitude = 113.3 + i * .00012, SourceElevation = 130 + i * 2 + Math.Sin(i * .08) * 100
                }).ToList() }]
            };
            var a = Make("主线·山脊徒步", 0, "#2877C7"); var b = Make("备选·林间步道", .0006, "#E25D35");
            var importTarget = new HikingProject(); var importParsed = new TrackImportResult();
            importParsed.Tracks.Add(Make("默认普通导入验证", 0, "#2877C7"));
            var importSession = ProjectImportSession.Create(importParsed, "默认普通导入验证.gpx", importTarget);
            var importDialog = new ImportWindow(importSession, importTarget, new ProjectImportService()) { Owner=window };
            importDialog.Show(); await Task.Delay(200);
            checks["导入窗口主攻略默认不选择且所有单选项未选中"] = importDialog.GuideTrackCombo.SelectedValue is null &&
                importDialog.TrackGrid.Items.Cast<object>().All(row => row.GetType().GetProperty("IsMain")?.GetValue(row) is false);
            importDialog.GuideTrackCombo.SelectedValue = importParsed.Tracks[0].Id;
            checks["导入窗口仍可手动选择主攻略轨迹"] = importSession.SelectedGuideTrackId == importParsed.Tracks[0].Id;
            importDialog.Close();
            a.RecalculateDistances(); b.RecalculateDistances(); project.AddTrack(a); project.AddTrack(b); project.MainGuideTrackId = a.Id;
            project.Settings.Map.Center = new(23.1003, 113.306); project.Settings.Map.Zoom = 16;
            var structure = new GuideStructureService(); var names = new[] { "起点：村口", "林间补水点", "山脊观景台", "营地", "终点：停车场" };
            for (var i = 0; i < 5; i++)
            { var point = a.Segments[0].Points[i * 25]; var node = new GuideNode { Name = names[i], TrackId = a.Id, OriginalCoordinate = point.Coordinate }; structure.SnapNode(node, a); project.GuideNodes.Add(node); }
            project.Waypoints.Add(new() { Name = "备选补给位置", AssociatedTrackId = b.Id, Latitude = 23.1009, Longitude = 113.3065 });
            GuideStructureService.ReplaceSegments(project, structure.RebuildSegments(project));
            project.GuideData.Settings = project.GuideData.Settings with { ElevationSourceKind = GuideElevationSourceKind.Gps, FlatDownSpeedKmh = 4, GentleUpSpeedKmh = 2.8, SteepUpSpeedKmh = 2 };
            var ids = project.GuideNodes.Select(n => n.Id).ToArray(); GuideDataReconciler.Reconcile(project.GuideData, a.Id, ids);
            for (var i = 1; i < ids.Length; i++)
            { var key = new GuideSegmentKey(a.Id, ids[i-1], ids[i]); project.GuideData.Manual.Segments[key.StorageKey] = new() { SegmentKey = key, PlannedDurationHours = .5, RoadCondition = "示例步道，请实际核实" }; }
            foreach (var plan in project.GuideData.Plans)
            { plan.DailyStartNodeIds = [ids[0], ids[2]]; plan.DepartureTimes[ids[0]] = new(7, 0); plan.DepartureTimes[ids[2]] = new(8, 30); plan.DailyNotes[ids[0]] = "示例备注：自行核实补给、天气与交通。"; }
            using (var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/AppLogo.png"))!.Stream)
            {
                using var memory = new MemoryStream(); await stream.CopyToAsync(memory); var bytes = memory.ToArray();
                var blob = new MediaBlob { PendingBytes = bytes, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), MimeType = "image/png", FileExtension = ".png", ByteLength = bytes.Length, Width = 1280, Height = 1280 };
                blob.StoredPath = $"media/originals/{blob.Id:D}.png";
                project.MediaBlobs.Add(blob); project.MediaAttachments.Add(new() { OwnerType = MediaOwnerType.GuideNode, OwnerId = ids[2], MediaBlobId = blob.Id, OriginalFileName = "软件Logo示例.png", SourceKind = MediaSourceKind.KmzEmbedded });
            }
            var projectPath = Path.Combine(folder, "演示项目.hikeproj"); await new ProjectFileService().SaveAsync(project, projectPath, true);
            await new GpxTrackExporter().ExportAsync(new TrackExportRequest { Tracks = [a], DocumentName = "导入演示" }, Path.Combine(folder, "import-demo.gpx"));
            await new KmlTrackExporter().ExportAsync(new TrackExportRequest { Tracks = [b], Waypoints = project.Waypoints, DocumentName = "导入演示" }, Path.Combine(folder, "import-demo.kml"));
            await main.OpenForAutomationAsync(projectPath); project = main.MapContextProject!; a = project.Tracks[0]; b = project.Tracks[1];
            window.Width = 1600; window.Height = 1000; window.BottomPanelRow.Height = new GridLength(350);
            for (var i = 0; i < 200 && !main.Map.IsReady; i++) await Task.Delay(100);
            await main.WaitForMapRefreshForAutomationAsync(); await Task.Delay(800);
            var web = FavoritesPanel.Descendants<WebView2>(window).First(w => w.Source?.Host == "hikingguide.local");
            async Task<JsonElement> Script(string code) => JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            Task<JsonElement> State() => Script("window.hikingGuideT10Test.inspect()");
            async Task Wait(Func<JsonElement, bool> predicate, string message)
            { for (var i = 0; i < 150; i++) { if (predicate(await State()) && !main.IsBusy) return; await Task.Delay(100); } throw new TimeoutException(message + ";当前:" + (await State()).GetRawText()); }
            Task Input(string type, double x, double y, string button = "none", int buttons = 0, int modifiers = 0) => web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button, buttons, modifiers, clickCount = type == "mouseMoved" ? 0 : 1 }));
            async Task Click(Guid id, bool right = false, double offset = 0, int modifiers = 0)
            { var p = await Script($"window.hikingGuideT10Test.location('{id:D}')"); var x = p.GetProperty("x").GetDouble(); var y = p.GetProperty("y").GetDouble() + offset; await Input("mouseMoved", x, y, modifiers: modifiers); await Input("mousePressed", x, y, right ? "right" : "left", right ? 2 : 1, modifiers); await Input("mouseReleased", x, y, right ? "right" : "left", modifiers: modifiers); await Task.Delay(500); }
            string Camera(JsonElement s) => s.GetProperty("camera").GetRawText();
            await Wait(s => s.GetProperty("trackIds").GetArrayLength() == 2, "两条轨迹未显示");
            checks["版本与窗口标题一致"] = SoftwareIdentity.DisplayVersion == "V1.9.5" && window.Title.Contains(SoftwareIdentity.DisplayVersion);
            var mapMenu=FavoritesPanel.Descendants<Menu>(window).Single().Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()=="地图");
            checks["地图菜单使用地图管理名称"]=mapMenu.Items.OfType<MenuItem>().Any(item=>item.Header?.ToString()=="地图管理")&&!mapMenu.Items.OfType<MenuItem>().Any(item=>item.Header?.ToString()=="图源管理");
            if (manual)
            {
                await main.SplitFixtureTrackForAutomationAsync(b.Id);
                var now = DateTimeOffset.UtcNow; var collection = await Favorites.SaveCollectionAsync(new(Guid.NewGuid(), "示例观景点", 1, now, now));
                await Favorites.SaveAsync(new() { Name = "山脊观景收藏点", Description = "演示收藏点，非实际路线建议。", Latitude = 23.1015, Longitude = 113.306, CollectionId = collection.Id }, []);
                await window.FavoritesContent.RefreshAsync();
                window.GuideWorkbench.SelectProject(project); await window.GuideWorkbench.CalculateAsync();
                await CaptureT10ManualAsync(window, main, folder);
                checks["实际界面截图已生成"] = File.Exists(Path.Combine(folder, "screenshots", "01-main.png")) && File.Exists(Path.Combine(folder, "screenshots", "18-guide.png"));
            }
            else
            {
                var mapView = FavoritesPanel.Descendants<MapView>(window).Single();
                var before = await State(); var camera = Camera(before);
                await Click(b.Id); checks["正常模式左击轨迹相机四项不变"] = Camera(await State()) == camera;
                await Click(a.Id); checks["普通模式仍可选中攻略分段且视角不变"] = main.SelectedAnalysisModel is GuideSegment && Camera(await State()) == camera;
                await Click(a.Id, true); checks["正常模式右击相机不变"] = Camera(await State()) == camera && mapView.ActiveTrackMenuForAutomation?.IsOpen == true;
                if (mapView.ActiveTrackMenuForAutomation is { } normalMenu) normalMenu.IsOpen = false;
                main.AutoConfirmTrackEditForAutomation = true; await main.EnterProjectEditForAutomationAsync(a.Id);
                await Wait(s => s.GetProperty("edit").ValueKind == JsonValueKind.Object && s.GetProperty("edit").GetProperty("trackId").GetString() == a.Id.ToString(), "主线编辑未进入");
                var firstSession = main.EditingSessionForAutomation; var aCoordinates = a.Segments.SelectMany(s => s.Points).Select(p => (p.Id, p.Latitude, p.Longitude)).ToArray();
                var manualData = JsonSerializer.Serialize(project.GuideData.Manual);
                await Click(b.Id, offset: 10);
                await Wait(s => main.EditingTrackIdForAutomation == b.Id && main.SelectedTrackEditPointId is not null && s.GetProperty("handleIds").GetArrayLength() > 0, "加宽容差跨轨迹选择失败");
                checks["一次进入跨轨迹编辑并显示句柄"] = main.IsTrackEditActive && main.EditingTrackIdForAutomation == b.Id && firstSession != main.EditingSessionForAutomation;
                checks["编辑左击相机四项不变"] = Camera(await State()) == camera;
                await Click(a.Id, true); await Wait(s => main.EditingTrackIdForAutomation == a.Id, "右击未切换编辑目标");
                checks["编辑右击换目标且菜单可用"] = main.IsTrackEditActive && mapView.ActiveTrackMenuForAutomation is { IsOpen: true, Tag: MapEventMessage context } && context.TrackId == a.Id && context.EditSessionId == main.EditingSessionForAutomation;
                checks["编辑右击相机四项不变"] = Camera(await State()) == camera;
                if (mapView.ActiveTrackMenuForAutomation is { } editMenu) editMenu.IsOpen = false;
                await Click(b.Id); await Wait(s => main.EditingTrackIdForAutomation == b.Id && main.SelectedTrackEditPointId is not null && s.GetProperty("handleIds").GetArrayLength() > 0, "普通轨迹句柄没有生成");
                var selectedId = main.SelectedTrackEditPointId!.Value; var point = project.Tracks.Single(t => t.Id == b.Id).Segments.SelectMany(s => s.Points).Single(p => p.Id == selectedId); var original = point.Coordinate;
                var oldSession = main.EditingSessionForAutomation; var oldRevision = b.GeometryRevision;
                var handle = await Script($"window.hikingGuideT10Test.handle('{selectedId:D}')"); var hx = handle.GetProperty("x").GetDouble(); var hy = handle.GetProperty("y").GetDouble();
                await Input("mousePressed", hx, hy, "left", 1); await Input("mouseMoved", hx + 20, hy + 15, "left", 1); await Task.Delay(200);
                var preview = await State(); checks["轨迹点拖动预览禁用地图平移"] = preview.GetProperty("dragging").GetBoolean() && !preview.GetProperty("panEnabled").GetBoolean();
                await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"Escape\",\"code\":\"Escape\",\"windowsVirtualKeyCode\":27}");
                await Input("mouseReleased", hx + 20, hy + 15, "left"); await Task.Delay(400);
                var cancel = await State(); checks["Esc取消预览恢复平移且不写坐标"] = !cancel.GetProperty("dragging").GetBoolean() && cancel.GetProperty("panEnabled").GetBoolean() && point.Coordinate == original;
                await Click(b.Id); await Wait(s => main.SelectedTrackEditPointId is not null && s.GetProperty("handleIds").GetArrayLength() > 0, "取消后句柄未恢复");
                selectedId = main.SelectedTrackEditPointId!.Value; point = project.Tracks.Single(t => t.Id == b.Id).Segments.SelectMany(s => s.Points).Single(p => p.Id == selectedId); original = point.Coordinate;
                handle = await Script($"window.hikingGuideT10Test.handle('{selectedId:D}')"); hx = handle.GetProperty("x").GetDouble(); hy = handle.GetProperty("y").GetDouble();
                await Input("mousePressed", hx, hy, "left", 1); await Input("mouseMoved", hx + 30, hy + 20, "left", 1); await Task.Delay(120); await Input("mouseReleased", hx + 30, hy + 20, "left");
                await Wait(s => project.Tracks.Single(t => t.Id == b.Id).GeometryRevision > oldRevision, "原生拖动未提交");
                var changed = project.Tracks.Single(t => t.Id == b.Id).Segments.SelectMany(s => s.Points).Single(p => p.Id == selectedId).Coordinate;
                checks["原生拖动只修改选中轨迹"] = changed != original && aCoordinates.SequenceEqual(project.Tracks.Single(t => t.Id == a.Id).Segments.SelectMany(s => s.Points).Select(p => (p.Id, p.Latitude, p.Longitude)));
                checks["人工攻略内容未被覆盖"] = manualData == JsonSerializer.Serialize(project.GuideData.Manual);
                var stale = MapProtocol.TryParseEvent(JsonSerializer.Serialize(new { protocolVersion = 1, type = "TrackPointDragEnded", projectId = project.Id, trackId = b.Id, geometryRevision = oldRevision, editSessionId = oldSession, trackPointId = selectedId, latitude = 20, longitude = 110 }))!;
                main.SendTrackEditEventForAutomation(stale); await Task.Delay(200);
                checks["旧会话拖动安全拒绝"] = project.Tracks.Single(t => t.Id == b.Id).Segments.SelectMany(s => s.Points).Single(p => p.Id == selectedId).Coordinate == changed;
                main.UndoCommand.Execute(null); await Task.Delay(700); await main.WaitForTrackEditTargetForAutomationAsync();
                checks["跨轨迹编辑撤销正确恢复"] = project.Tracks.Single(t => t.Id == b.Id).Segments.SelectMany(s => s.Points).Single(p => p.Id == selectedId).Coordinate == original && main.IsTrackEditActive;
                main.RedoCommand.Execute(null); await Task.Delay(700); await main.WaitForTrackEditTargetForAutomationAsync();
                checks["重做后继续保持项目编辑模式"] = project.Tracks.Single(t => t.Id == b.Id).Segments.SelectMany(s => s.Points).Single(p => p.Id == selectedId).Coordinate == changed && main.IsTrackEditActive;
                checks["编辑提交撤销重做没有移动地图"] = Camera(await State()) == camera;
                await main.EndProjectEditForAutomationAsync(); await Wait(s => s.GetProperty("edit").ValueKind == JsonValueKind.Null, "编辑退出未同步");
                checks["退出编辑恢复正常地图"] = (await State()).GetProperty("panEnabled").GetBoolean();
                await Click(b.Id); await Click(a.Id, modifiers: 2);
                await Wait(s => s.GetProperty("selectedTrackIds").GetArrayLength() == 2, "Ctrl 整轨多选未同步");
                checks["普通模式Ctrl整轨多选全部高亮且相机不变"] =
                    (await State()).GetProperty("selectedTrackFilter")[2][1].GetArrayLength() == 2 && Camera(await State()) == camera;
                await Click(b.Id, modifiers: 2);
                await Wait(s => s.GetProperty("selectedTrackIds").GetArrayLength() == 1, "Ctrl 整轨减选未同步");
                checks["Ctrl再次点击取消整轨选择"] = (await State()).GetProperty("selectedTrackIds")[0].GetString() == a.Id.ToString();
                await main.EnterProjectEditForAutomationAsync(a.Id);
                main.AddTrackPointCommand.Execute(null); var pointsBeforeMulti = b.GetPointCount();
                await Click(b.Id, modifiers: 2);
                await Wait(s => s.GetProperty("selectedTrackIds").GetArrayLength() == 2, "编辑 Ctrl 多选未同步");
                checks["编辑模式Ctrl多选不误执行插点且视角不变"] = b.GetPointCount() == pointsBeforeMulti && Camera(await State()) == camera;
                await main.EndProjectEditForAutomationAsync();
                await Click(a.Id);
                checks["主攻略轨迹合并连接命令不可执行"] = !main.CreateMergedTrackCommand.CanExecute(null) && !main.CreateConnectedTrackCommand.CanExecute(null);
                await main.ChangeMainTrackForAutomationAsync(null); await main.WaitForMapRefreshForAutomationAsync();
                checks["主攻略转普通同步节点及图片归属"] = project.MainGuideTrackId is null && project.GuideNodes.Count == 0 && project.Waypoints.Count(w => w.AssociatedTrackId == a.Id) == 5 && project.MediaAttachments.Single().OwnerType == MediaOwnerType.Waypoint;
                await main.SplitFixtureTrackForAutomationAsync(b.Id); await Task.Delay(400);
                var sections = b.GetSplitSections();
                await Click(a.Id, modifiers: 1); await Click(sections[0].Id, modifiers: 2);
                await Wait(s => s.GetProperty("selectedTrackIds").GetArrayLength() == 1 && main.SelectedSplitSectionIds.Count == 1, "整轨与分段混选失败");
                checks["地图整轨与拆分分段混选并同步高亮"] = (await State()).GetProperty("selectedSectionFilter")[2][1].GetArrayLength() == 1;
                await Click(sections[1].Id, modifiers: 2);
                await Wait(s => main.SelectedSplitSectionIds.Count == 2, "拆分分段加选失败");
                checks["Ctrl多个拆分分段同时高亮"] = (await State()).GetProperty("selectedSectionFilter")[2][1].GetArrayLength() == 2;
                await Click(sections[0].Id, modifiers: 2);
                checks["Ctrl再次点击取消分段且保留其他选中项"] = main.SelectedSplitSectionIds.Count == 1 && (await State()).GetProperty("selectedTrackIds").GetArrayLength() == 1;
                await Click(sections[1].Id, right: true);
                checks["右击已选分段保持混合多选及地图视角"] = main.SelectedSplitSectionIds.Count == 1 && (await State()).GetProperty("selectedTrackIds").GetArrayLength() == 1 && Camera(await State()) == camera;
                var menuHeaders = mapView.ActiveTrackMenuForAutomation!.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray();
                checks["地图右键只保留统一合并连接入口"] = menuHeaders.Count(item => item == "合并轨迹") == 1 && menuHeaders.Count(item => item == "连接轨迹") == 1 && !menuHeaders.Any(item => item?.Contains("相邻分段") == true || item?.Contains("为新轨迹") == true);
                mapView.ActiveTrackMenuForAutomation.IsOpen = false;
                async Task Combine(bool connect, bool deleteSources, string name)
                {
                    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; var attempts = 0;
                    timer.Tick += (_, _) =>
                    {
                        var dialog = Windows.OfType<TrackMergeWindow>().LastOrDefault();
                        if (dialog is null) { if (++attempts > 100) { timer.Stop(); completed.TrySetException(new TimeoutException("统一轨迹窗口未打开")); } return; }
                        timer.Stop();
                        checks[(connect ? "连接" : "合并") + "窗口默认勾选删除来源且预选混合范围"] = dialog.DeleteOriginalTracks && dialog.SelectedSources.Count == 2 && dialog.SelectedSources.Any(source => source.SectionId.HasValue);
                        dialog.NameBox.Text = name; dialog.DeleteOriginalCheck.IsChecked = deleteSources;
                        dialog.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); completed.TrySetResult();
                    };
                    timer.Start();
                    (connect ? main.CreateConnectedTrackCommand : main.CreateMergedTrackCommand).Execute(null);
                    await completed.Task;
                    await Wait(s => project.Tracks.Any(track => track.Name == name), "统一轨迹命令没有生成新轨迹");
                }
                var bBefore = new HikingGuide.Core.Services.TrackSnapService().GetGeometryFingerprint(b);
                await Combine(false, true, "原生混合合并成果");
                checks["统一合并命令只删除选中分段并保留标注"] = !project.Tracks.Contains(a) && project.Tracks.Contains(b) && b.GetSplitSections().Count == 1 && project.GuideNodes.Count == 0 && project.Waypoints.Count == 6 && project.MediaAttachments.Count == 1;
                var mergedTrack = project.Tracks.Single(t => t.Name == "原生混合合并成果");
                checks["真实混合合并将被删除分段标注移到新轨迹"] = project.Waypoints.All(w => w.AssociatedTrackId == mergedTrack.Id);
                await main.ChangeMainTrackForAutomationAsync(mergedTrack.Id); await main.WaitForMapRefreshForAutomationAsync();
                checks["编辑成果设为主攻略同步全部标注及图片"] = project.MainGuideTrackId == mergedTrack.Id && project.GuideNodes.Count == 6 && project.Waypoints.Count == 0 && project.MediaAttachments.Single().OwnerType == MediaOwnerType.GuideNode;
                checks["新主攻略节点重新吸附且人工内容保留归档"] = project.GuideNodes.All(n => !n.IsOrphaned && n.ExactPosition?.TrackId == mergedTrack.Id) &&
                    project.GuideData.Manual.Segments.Values.Concat(project.GuideData.Manual.ArchivedSegments.Select(x => x.Data)).Count(x => x.RoadCondition == "示例步道，请实际核实" && x.PlannedDurationHours == .5) == 4;
                var testPlan = project.GuideData.Plans[0]; var notesBeforeGuard = testPlan.DailyNotes[ids[0]];
                testPlan.DailyNotes[ids[0]] = "转换后新增内容，不能被撤销覆盖";
                main.UndoCommand.Execute(null);
                checks["撤销遇到新增人工内容安全拒绝且历史仍保留"] = project.MainGuideTrackId == mergedTrack.Id && testPlan.DailyNotes[ids[0]] == "转换后新增内容，不能被撤销覆盖" && main.UndoCommand.CanExecute(null);
                testPlan.DailyNotes[ids[0]] = notesBeforeGuard;
                main.UndoCommand.Execute(null); await main.WaitForMapRefreshForAutomationAsync();
                checks["主攻略转换可一次撤销为普通成果"] = project.MainGuideTrackId is null && project.GuideNodes.Count == 0 && project.Waypoints.Count == 6;
                main.UndoCommand.Execute(null); await main.WaitForMapRefreshForAutomationAsync(); await Task.Delay(400);
                checks["统一合并一次撤销恢复完整普通来源"] = project.Tracks.Contains(a) && project.MainGuideTrackId is null && new HikingGuide.Core.Services.TrackSnapService().GetGeometryFingerprint(b) == bBefore;
                checks["标注迁移与轨迹删除一次撤销恢复原归属"] = project.Waypoints.Count(w => w.AssociatedTrackId == a.Id) == 5 && project.Waypoints.Single(w => w.Name == "备选补给位置").AssociatedTrackId == b.Id;
                await Click(a.Id, modifiers: 1); await Click(sections[1].Id, modifiers: 2);
                await Combine(true, false, "原生混合连接成果");
                checks["统一连接取消删除保留来源且生成直线"] = project.Tracks.Contains(a) && project.Tracks.Contains(b) && project.Tracks.Single(track => track.Name == "原生混合连接成果").Segments.Any(part => part.Name == "人工直线连接");
                main.UndoCommand.Execute(null); await main.WaitForMapRefreshForAutomationAsync(); await Task.Delay(300);
                checks["多选与统一操作完成后视角不变"] = Camera(await State()) == camera;
                await main.ChangeMainTrackForAutomationAsync(a.Id); await main.WaitForMapRefreshForAutomationAsync();
                var batchMarkers = project.GuideNodes.Take(2).Cast<MapMarkerBase>().Concat(project.Waypoints.Take(1)).ToArray();
                // 准备混合选择后执行正式批量命令；用真实WPF按钮提交对话框。
                var selectedItems = T10ManualField<HashSet<object>>(main, "_selectedProjectItems");
                selectedItems.Clear(); foreach (var m in batchMarkers) selectedItems.Add(m);
                var beforeNames = batchMarkers.Select(m => m.Name).ToArray();
                var beforeDescriptions = batchMarkers.Select(m => m.Description).ToArray();
                var completedBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var batchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; var batchAttempts = 0;
                batchTimer.Tick += (_, _) =>
                {
                    var dialog = Windows.OfType<BatchMarkerEditorWindow>().LastOrDefault();
                    if (dialog is null) { if (++batchAttempts > 100) { batchTimer.Stop(); completedBatch.TrySetException(new TimeoutException("批量编辑窗口未打开")); } return; }
                    batchTimer.Stop(); dialog.ApplyDescription.IsChecked = true; dialog.DescriptionBox.Text = "原生批量编辑验收描述";
                    checks["批量窗口初始不勾选名称与类型"] = dialog.ApplyName.IsChecked != true && dialog.ApplyType.IsChecked != true;
                    dialog.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); completedBatch.TrySetResult();
                };
                batchTimer.Start(); main.BatchEditMarkersCommand.Execute(null); await completedBatch.Task;
                for (var wait = 0; main.IsBusy && wait < 100; wait++) await Task.Delay(100);
                if (main.IsBusy) throw new TimeoutException("批量编辑提交超时");
                checks["批量真实对话框仅修改勾选字段"] = batchMarkers.All(m => m.Description == "原生批量编辑验收描述") && beforeNames.SequenceEqual(batchMarkers.Select(m => m.Name));
                main.UndoCommand.Execute(null); await main.WaitForMapRefreshForAutomationAsync();
                checks["批量修改一次撤销恢复不同原内容"] = beforeDescriptions.SequenceEqual(batchMarkers.Select(m => m.Description));
                async Task SubmitBatch(Action<BatchMarkerEditorWindow> configure)
                {
                    selectedItems.Clear(); foreach (var marker in batchMarkers) selectedItems.Add(marker);
                    var timer = new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var tries=0;
                    timer.Tick += (_,_) =>
                    {
                        var dialog=Windows.OfType<BatchMarkerEditorWindow>().LastOrDefault();
                        if(dialog is null) { if(++tries>100) { timer.Stop(); ready.TrySetException(new TimeoutException("批量身份窗口未打开")); } return; }
                        timer.Stop(); configure(dialog); dialog.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); ready.TrySetResult();
                    };
                    timer.Start(); main.BatchEditMarkersCommand.Execute(null); await ready.Task;
                    for(var wait=0;main.IsBusy && wait<100;wait++) await Task.Delay(100);
                    if(main.IsBusy) throw new TimeoutException("批量身份修改提交超时");
                }
                var selectedIds=batchMarkers.Select(m=>m.Id).ToHashSet();
                await SubmitBatch(dialog => { dialog.ApplyGuideState.IsChecked=true; dialog.GuideCheck.IsChecked=true; });
                checks["批量设为攻略节点窗口提交及主轨迹归属"] = selectedIds.All(id=>project.GuideNodes.Any(n=>n.Id==id && n.TrackId==a.Id)) && !project.Waypoints.Any(w=>selectedIds.Contains(w.Id));
                main.UndoCommand.Execute(null); await main.WaitForMapRefreshForAutomationAsync();
                await SubmitBatch(dialog => { dialog.ApplyGuideState.IsChecked=true; dialog.GuideCheck.IsChecked=false; dialog.ApplyTrack.IsChecked=true; dialog.TrackCombo.SelectedValue=b.Id; });
                checks["批量转普通标注并指定所属轨迹窗口提交"] = selectedIds.All(id=>project.Waypoints.Any(w=>w.Id==id && w.AssociatedTrackId==b.Id)) && !project.GuideNodes.Any(n=>selectedIds.Contains(n.Id));
                main.UndoCommand.Execute(null); await main.WaitForMapRefreshForAutomationAsync();
                checks["批量身份与归属一次撤销恢复混合原对象"] = batchMarkers.All(m=>m is Waypoint w ? project.Waypoints.Contains(w) : project.GuideNodes.Contains((GuideNode)m)) && beforeNames.SequenceEqual(batchMarkers.Select(m=>m.Name));

                var ownedProbe=new Waypoint { Name="隐藏归属验证",Latitude=23.1,Longitude=113.305,AssociatedTrackId=a.Id };
                project.Waypoints.Add(ownedProbe);
                selectedItems.Clear(); selectedItems.Add(a); main.HideSelectedTracksCommand.Execute(null);
                await main.WaitForMapRefreshForAutomationAsync(); await Task.Delay(500);
                var hiddenMarkers=await State();
                checks["项目面板隐藏轨迹同步隐藏普通标注与攻略节点"] =
                    !hiddenMarkers.GetProperty("waypointIds").EnumerateArray().Any(id=>id.GetString()==ownedProbe.Id.ToString("D")) &&
                    !hiddenMarkers.GetProperty("guideNodeIds").EnumerateArray().Any(id=>project.GuideNodes.Any(n=>n.TrackId==a.Id && n.Id.ToString("D")==id.GetString()));
                main.UndoCommand.Execute(null); await main.WaitForMapRefreshForAutomationAsync(); await Task.Delay(500);
                checks["撤销隐藏恢复所属标注且原数据保留"] = (await State()).GetProperty("waypointIds").EnumerateArray().Any(id=>id.GetString()==ownedProbe.Id.ToString("D")) && project.Waypoints.Contains(ownedProbe);
                project.Waypoints.Remove(ownedProbe);
                async Task CheckAddEditor(MapMarkerEditKind kind)
                {
                    var count=kind==MapMarkerEditKind.AddWaypoint ? project.Waypoints.Count : project.GuideNodes.Count;
                    var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) }; var attempts=0;
                    timer.Tick+=(_,_)=>
                    {
                        var editor=Windows.OfType<MarkerEditorWindow>().LastOrDefault();
                        if(editor is null) { if(++attempts>100){timer.Stop();ready.TrySetException(new TimeoutException("新点编辑窗口未打开"));} return; }
                        timer.Stop(); checks[kind==MapMarkerEditKind.AddWaypoint ? "新建普通标注立即弹出编辑窗口" : "新建攻略节点立即弹出编辑窗口"] =
                            (kind==MapMarkerEditKind.AddWaypoint ? project.Waypoints.Count : project.GuideNodes.Count)==count+1;
                        editor.Close(); ready.TrySetResult();
                    };
                    timer.Start(); main.AddMarkerForAutomation(kind,new(23.1,113.3048)); await ready.Task;
                    await Task.Delay(200); main.UndoCommand.Execute(null); await main.WaitForMapRefreshForAutomationAsync();
                }
                await CheckAddEditor(MapMarkerEditKind.AddWaypoint); await CheckAddEditor(MapMarkerEditKind.AddGuideNode);

                var realFile = @"F:\徒步规划项目\测试合并轨迹\南迦巴瓦山脊C线.kmz";
                if (!File.Exists(realFile)) throw new FileNotFoundException("本次实际KMZ验证文件不存在", realFile);
                var hashBefore = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(realFile)));
                var files = T10ManualField<ITrackFileService>(main, "_trackFiles");
                var raw = await Task.Run(() => files.ImportAsync(realFile));
                var grouped = await Task.Run(() => new ProjectImportBatch(files).ReadAsync([realFile], new HikingProject()));
                if (!raw.IsSuccess || !grouped.IsSuccess) throw new InvalidOperationException(string.Join("；", raw.Errors.Concat(grouped.Errors).Select(e => e.Message)));
                var resultTrack = grouped.Tracks.Single();
                checks["实际KMZ原名称单父轨迹与内部拆分分段"] = resultTrack.Name == "南迦巴瓦山脊C线" && resultTrack.GetSplitSections().Count == raw.Tracks.Count && raw.Tracks.Select(t => t.Name).SequenceEqual(resultTrack.GetSplitSections().Select(s => s.Name));
                checks["实际KMZ原始点断点距离与图片链接保持"] = resultTrack.GetPointCount() == raw.Tracks.Sum(t => t.GetPointCount()) && resultTrack.Segments.Count == raw.Tracks.Sum(t => t.Segments.Count) && Math.Abs(resultTrack.GetTotalLength() - raw.Tracks.Sum(t => t.GetTotalLength())) < .000001 && grouped.MediaAttachments.Count == raw.MediaAttachments.Count && grouped.ExternalMediaReferences.Count == raw.ExternalMediaReferences.Count;
                checks["实际KMZ标注归属父轨迹且来源文件不变"] = grouped.Waypoints.All(w => w.AssociatedTrackId == resultTrack.Id) && hashBefore == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(realFile)));
                var importedProject = new HikingProject { Name = "真实KMZ临时往返验证" };
                var importedSession = ProjectImportSession.Create(grouped, realFile, importedProject);
                new ProjectImportService().Commit(importedSession, importedProject, true);
                var savedFile = Path.Combine(folder, "真实KMZ临时往返验证.hikeproj");
                var storage = new ProjectFileService(); await storage.SaveAsync(importedProject, savedFile, false);
                var reopened = await storage.LoadAsync(savedFile);
                checks["实际KMZ保存重开保持分段标注及图片引用"] = reopened.Tracks.Count == 1 && reopened.Tracks[0].Name == resultTrack.Name && reopened.Tracks[0].GetSplitSections().Select(s => s.Name).SequenceEqual(resultTrack.GetSplitSections().Select(s => s.Name)) && reopened.Waypoints.All(w => w.AssociatedTrackId == resultTrack.Id) && reopened.Waypoints.Count == 59 && reopened.ExternalMediaReferences.Count == 57;
                diagnostics.Add(new { RealKmz = realFile, OriginalTracks = raw.Tracks.Count, ImportedTracks = grouped.Tracks.Count, SplitSections = resultTrack.GetSplitSections().Count, Points = resultTrack.GetPointCount(), Markers = grouped.Waypoints.Count, Images = grouped.MediaAttachments.Count, ExternalImages = grouped.ExternalMediaReferences.Count, DistanceMeters = resultTrack.GetTotalLength(), SourceSHA256 = hashBefore });
                var connectedFile=@"F:\徒步规划项目\船底顶.hikeproj";
                var connectedHash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(connectedFile)));
                var connectedProject=await storage.LoadAsync(connectedFile);
                var connectedTrack=connectedProject.Tracks.Single(t=>t.Name=="船底顶-联A西A");
                var realBranchCoordinate=connectedTrack.Segments[0].Points[Math.Min(120,connectedTrack.Segments[0].Points.Count-1)].Coordinate;
                var realBranches=await Task.Run(()=>new OverlapSnapService().Find(connectedTrack,realBranchCoordinate));
                checks["真实船底顶重合路段提供不同里程候选"] = realBranches.Count>=2 && realBranches[^1].DistanceFromTrackStart-realBranches[0].DistanceFromTrackStart>connectedTrack.GetTotalLength()/2;
                var overlapTrack=new Track {Name="重合去程回程验证",Segments=[new() {Points=[new() {Latitude=23.1,Longitude=113.3},new() {Latitude=23.1,Longitude=113.312},new() {Latitude=23.11,Longitude=113.312},new() {Latitude=23.1,Longitude=113.312},new() {Latitude=23.1,Longitude=113.3}]}]};
                overlapTrack.RecalculateDistances();project.AddTrack(overlapTrack,false);
                var overlapPoint=new Waypoint {Name="选择回程验证",Latitude=23.1,Longitude=113.3048,AssociatedTrackId=overlapTrack.Id};project.Waypoints.Add(overlapPoint);
                var branchTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(100)}; var branchAttempts=0;
                branchTimer.Tick+=(_,_)=>
                {
                    var picker=Windows.OfType<SnapBranchWindow>().LastOrDefault();
                    if(picker is null){if(++branchAttempts>100)branchTimer.Stop();return;}
                    branchTimer.Stop();picker.CandidateGrid.SelectedIndex=picker.CandidateGrid.Items.Count-1;
                    picker.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                branchTimer.Start();await main.SnapMapMarkerAsync(overlapPoint,true,true);
                checks["真实选择吸附窗口选中回程且保存完整位置"] = overlapPoint.PreferredTrackPosition is { } selectedPosition && selectedPosition.DistanceFromTrackStart>overlapTrack.GetTotalLength()/2;
                var preferredMileage=overlapPoint.PreferredTrackPosition!.DistanceFromTrackStart;
                await main.SnapMapMarkerAsync(overlapPoint,true);
                checks["普通再次吸附不覆盖用户回程选择"] = overlapPoint.PreferredTrackPosition!.DistanceFromTrackStart==preferredMileage;
                await main.ChangeMainTrackForAutomationAsync(overlapTrack.Id);
                var overlapGuide=project.GuideNodes.Single(n=>n.Id==overlapPoint.Id);
                checks["已选回程普通标注提升为攻略节点仍使用回程里程"] = overlapGuide.ExactPosition!.DistanceFromTrackStart==preferredMileage;
                branchAttempts=0;branchTimer.Start();await main.SnapMapMarkerAsync(overlapGuide,false,true);
                checks["攻略节点真实窗口选择回程路段"] = overlapGuide.PreferredTrackPosition!.DistanceFromTrackStart==preferredMileage;
                main.UndoCommand.Execute(null);main.UndoCommand.Execute(null);
                main.UndoCommand.Execute(null);main.UndoCommand.Execute(null);
                checks["选择吸附路段可撤销回到未选择状态"] = overlapPoint.PreferredTrackPosition is null;
                project.Waypoints.Remove(overlapPoint);project.Tracks.Remove(overlapTrack);
                var originalPartCount=connectedTrack.Segments.Count; var originalPointCount=connectedTrack.GetPointCount();
                var originalDistance=connectedTrack.GetTotalLength();
                foreach(var format in new[]{TrackFileFormat.Gpx,TrackFileFormat.Kml})
                {
                    var exportPath=Path.Combine(folder,"船底顶-联A西A-兼容验证."+format.ToString().ToLowerInvariant());
                    var selection=new MergedExportSelection(); selection.TrackIds.Add(connectedTrack.Id);
                    await new ProjectMergedExportService().ExportAsync(connectedProject,selection,format,exportPath,false);
                    var xml=System.Xml.Linq.XDocument.Load(exportPath);
                    var topName = format==TrackFileFormat.Gpx
                        ? xml.Root!.Elements().First(e=>e.Name.LocalName=="metadata").Elements().First(e=>e.Name.LocalName=="name").Value
                        : xml.Root!.Elements().First(e=>e.Name.LocalName=="Document").Elements().First(e=>e.Name.LocalName=="name").Value;
                    checks[$"真实轨迹{format}顶层名称不再使用项目名"] = topName==connectedTrack.Name;
                    checks[$"真实连接轨迹{format}导出仅一个几何段"] = xml.Descendants().Count(e=>e.Name.LocalName==(format==TrackFileFormat.Gpx?"trkseg":"LineString"))==1;
                    var parsedBack=format==TrackFileFormat.Gpx ? await new GpxTrackImporter().ImportAsync(exportPath) : await new KmlTrackImporter().ImportAsync(exportPath);
                    checks[$"真实连接轨迹{format}往返完整点序列"] = parsedBack.Tracks.Count==1 && parsedBack.Tracks[0].Segments.Count==1 &&
                        parsedBack.Tracks[0].Segments[0].Points.Select(p=>(p.Latitude,p.Longitude)).SequenceEqual(connectedTrack.Segments.SelectMany(s=>s.Points).Select(p=>(p.Latitude,p.Longitude)));
                }
                checks["真实连接项目未改写且内部结构统计不变"] = connectedHash==Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(connectedFile))) &&
                    connectedTrack.Segments.Count==originalPartCount && connectedTrack.GetTotalLength()==originalDistance;
                diagnostics.Add(new {ConnectedProject=connectedFile,ConnectedTrack=connectedTrack.Name,OriginalParts=originalPartCount,Points=originalPointCount,ProjectDistance=originalDistance,ProjectSHA256=connectedHash});
                diagnostics.Add(new { Initial = before, Final = await State(), SourcePoints = a.GetPointCount() + b.GetPointCount() });
                var cacheSource=new MapSourceDefinition {Name="缓存容量界面验证",UrlTemplate="https://example.com/{z}/{x}/{y}.png"};
                var cacheEditor=new MapSourceEditorWindow(cacheSource,MapSourceTemplateService) {Owner=window};
                checks["图源新增缓存容量默认500MB"] = cacheEditor.CacheCapacityText.Text=="500";
                var cacheTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(150)};
                cacheTimer.Tick+=(_,_)=> {cacheTimer.Stop();cacheEditor.CacheCapacityText.Text="123";FavoritesPanel.Descendants<Button>(cacheEditor).Single(button=>button.Content?.ToString()=="确定").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));};
                cacheTimer.Start();cacheEditor.ShowDialog();
                checks["图源编辑缓存容量输入确认生效"] = cacheSource.CacheCapacityMb==123;
                var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
                var port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;using var tileStop=new CancellationTokenSource();int tileDownloads=0;
                byte[] tileBytes;using(var resource=GetResourceStream(new Uri("pack://application:,,,/Assets/AppLogo.png"))!.Stream){using var bytes=new MemoryStream();resource.CopyTo(bytes);tileBytes=bytes.ToArray();}
                var tileServer=Task.Run(async()=>
                {
                    try
                    {
                        while(!tileStop.IsCancellationRequested)
                        {
                            using var client=await listener.AcceptTcpClientAsync(tileStop.Token);using var stream=client.GetStream();using var reader=new StreamReader(stream,System.Text.Encoding.ASCII,false,1024,true);
                            while(await reader.ReadLineAsync(tileStop.Token) is {} line && line.Length>0){}
                            Interlocked.Increment(ref tileDownloads);
                            var header=System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nCache-Control: max-age=3600\r\nContent-Length: {tileBytes.Length}\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(header,tileStop.Token);await stream.WriteAsync(tileBytes,tileStop.Token);
                        }
                    }
                    catch(Exception e) when(tileStop.IsCancellationRequested && e is OperationCanceledException or System.Net.Sockets.SocketException){}
                });
                try
                {
                    var source=new MapSourceDefinition {Name="瓦片缓存隔离验证",UrlTemplate=$"http://127.0.0.1:{port}/{{z}}/{{x}}/{{y}}.png"};
                    var bridge=T10ManualField<HikingGuide.App.Services.WebViewMapBridge>(mapView,"_bridge");
                    var slots=bridge.ConfigureRasterLayers([new RasterLayerSlot("base",source,[source.UrlTemplate],true,1)]);
                    var virtualTile=slots[0].Templates[0].Replace("{z}","1").Replace("{x}","0").Replace("{y}","0");
                    var script=$"fetch({JsonSerializer.Serialize(virtualTile)}).then(async r=>{{if(!r.ok)throw new Error('瓦片请求失败');window.hikingGuideCacheTestPolicy=r.headers.get('cache-control');window.hikingGuideCacheTestSize=(await r.arrayBuffer()).byteLength;}}).catch(()=>window.hikingGuideCacheTestSize=-1)";
                    for(var attempt=0;attempt<2;attempt++)
                    {
                        await Script("window.hikingGuideCacheTestSize=0;"+script);
                        for(var wait=0;wait<200;wait++){var size=(await Script("window.hikingGuideCacheTestSize")).GetInt32();if(size!=0)break;await Task.Delay(50);}
                        if((await Script("window.hikingGuideCacheTestSize")).GetInt32()!=tileBytes.Length)throw new InvalidOperationException("原生瓦片缓存请求失败。");
                    }
                    checks["原生WebView重复瓦片仅向服务器下载一次"] = tileDownloads==1;
                    checks["原生瓦片缓存写入隔离Data目录"] = Directory.EnumerateFiles(Path.Combine(Environment.GetEnvironmentVariable("HIKINGGUIDE_APPDATA_DIR")!,"MapTileCache",source.Id.ToString("N")),"*.tile").Any();
                    checks["T160在线瓦片不另存浏览器磁盘缓存"]=(await Script("window.hikingGuideCacheTestPolicy")).GetString()=="no-store";
                    await HikingGuide.Infrastructure.Maps.MapTileDiskCache.Shared(Path.Combine(Environment.GetEnvironmentVariable("HIKINGGUIDE_APPDATA_DIR")!,"MapTileCache")).ClearAsync(source.Id);
                    await Script("window.hikingGuideCacheTestSize=0;"+script);
                    for(var wait=0;wait<200;wait++){if((await Script("window.hikingGuideCacheTestSize")).GetInt32()!=0)break;await Task.Delay(50);}
                    checks["T160清空后同一URL重新下载而非复用浏览器旧缓存"]=tileDownloads==2&&(await Script("window.hikingGuideCacheTestSize")).GetInt32()==tileBytes.Length;
                    bridge.ConfigureRasterLayers(MapLayerStackService.Build(GlobalSettingsService.Current.MapLayerStack!,GlobalSettingsService.Current.MapSources));
                }
                finally {tileStop.Cancel();listener.Stop();await tileServer;}
                if(DemCatalog.Databases.Count>0)await RunTerrainSmokeAsync(window,main,web,folder,checks,diagnostics);
                if(Environment.GetEnvironmentVariable("HIKINGGUIDE_P102_TEST")=="1")await RunP102SmokeAsync(window,main,web,folder,checks,diagnostics);
                await RunT160SmokeAsync(window,main,web,folder,checks);
                await CaptureT10ClientAsync(window, Path.Combine(folder, "T1.0原生界面验收.png"));
            }
        }
        catch (Exception exception) { error = exception.ToString(); }
        var passed = error is null && checks.Count > 0 && checks.Values.All(v => v);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Passed = passed, Version = SoftwareIdentity.DisplayVersion, Checks = checks, Error = error, Diagnostics = diagnostics }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return passed;
    }
}





