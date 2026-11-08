using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Guides;
using HikingGuide.Application.Media;
using HikingGuide.Application.Maps;
using HikingGuide.Application.Projects;
using HikingGuide.Application.Routing;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Track = HikingGuide.Core.Models.Track;

namespace HikingGuide.App;

public partial class App
{
    private sealed record T10ManualShot(string File, string Module, bool Success,
        string? WindowTitle, string? Note, string? Error);

    /// <summary>仅在隔离演示会话拍摄已实现的窗口，不操作保存、删除或下载按钮。</summary>
    private async Task CaptureT10ManualAsync(MainWindow window, MainViewModel main, string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        if (!root.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(GlobalSettingsService.SettingsFilePath).StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(Favorites.Paths.FavoritesRoot).StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("操作说明截图必须隔离设置、收藏库和输出目录，不能访问正式配置。");
        var project = main.MapContextProject ?? throw new InvalidOperationException("请先加载隔离演示项目。");
        var projectService = T10ManualField<IProjectService>(main, "_projects");
        if (projectService.OpenProjects.Any(context => !string.IsNullOrWhiteSpace(context.FilePath) &&
            !Path.GetFullPath(context.FilePath).StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("截图会话只能打开临时演示项目，不允许正式项目自动保存。");
        var directory = Path.Combine(root, "screenshots");
        Directory.CreateDirectory(directory);
        var shots = new List<T10ManualShot>();

        async Task Shot(string file, string module, Func<Task<(Window Target, string? Note)>> prepare)
        {
            Window? target = null;
            try
            {
                var prepared = await prepare(); target = prepared.Target;
                await T10ManualLayoutAsync(target);
                await CaptureT10ClientAsync(target, Path.Combine(directory, file));
                shots.Add(new("screenshots/" + file, module, true, target.Title, prepared.Note, null));
            }
            catch (Exception error)
            {
                shots.Add(new("screenshots/" + file, module, false, target?.Title, null,
                    error.GetBaseException().Message));
            }
            finally
            {
                if (target is not null && target != window)
                {
                    // 图层管理器关闭时原本会保存。只拍照且没有编辑，跳过这一自动保存。
                    if (target is MapSourceManagerWindow)
                        typeof(MapSourceManagerWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, true);
                    target.Close();
                    await T10ManualLayoutAsync(window);
                }
            }
        }
        Task<(Window, string?)> Show(Window target, string? note = null)
        { target.Owner = window; target.WindowStartupLocation = WindowStartupLocation.CenterOwner; target.Show(); return Task.FromResult((target, note)); }
        async Task<(Window, string?)> Tab(int index, string? note = null)
        { window.BottomTabs.SelectedIndex = index; await T10ManualLayoutAsync(window); return (window, note); }

        window.WindowState = WindowState.Normal;
        window.Width = Math.Min(1680, Math.Max(1100, SystemParameters.WorkArea.Width - 40));
        window.Height = Math.Min(1040, Math.Max(720, SystemParameters.WorkArea.Height - 60));
        foreach (var node in T10ManualNodes(main.NavigationNodes))
            node.IsExpanded = node.Model is HikingProject or Track || node.Children.Any(child => child.Model is Track);
        var track = project.Tracks.FirstOrDefault(item => item.Id == project.MainGuideTrackId) ?? project.Tracks.First();
        main.SelectNavigationNode(new NavigationNode(track.Name ?? "轨迹", track), focusOnMap: false);
        if (main.IsTrackEditActive) main.ExitTrackEditCommand.Execute(null);
        await main.WaitForMapRefreshForAutomationAsync();
        var mainWeb = FavoritesPanel.Descendants<WebView2>(window).First(item => item.Source?.Host == "hikingguide.local");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await mainWeb.CoreWebView2.ExecuteScriptAsync("window.hikingGuideRasterTest.inspect().slots[0].loaded") == "true") break;
            await Task.Delay(150);
        }
        await Shot("01-main.png", "主界面与多轨迹项目", () => Tab(0,
            "真实 T1.0 客户端；WPF 内容与 WebView2 地图按实际客户端位置合成，窗口标题另记录，不绘制标题栏。"));

        await Shot("02-import.png", "统一导入预览", async () =>
        {
            var paths = Directory.EnumerateFiles(root, "import-demo.*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".gpx" or ".kml" or ".kmz").ToArray();
            if (paths.Length == 0) throw new InvalidOperationException("没有实际 import-demo.gpx/kml/kmz 文件，未伪造导入预览。");
            var files = T10ManualField<ITrackFileService>(main, "_trackFiles");
            var parsed = await new ProjectImportBatch(files).ReadAsync(paths, project);
            if (!parsed.IsSuccess) throw new InvalidOperationException(string.Join("；", parsed.Errors.Select(error => error.Message)));
            var session = ProjectImportSession.Create(parsed, string.Join("; ", paths), project);
            var import = new ProjectImportService(); await Task.Run(() => import.Preview(session, project));
            return await Show(new ImportWindow(session, project, import, _externalMediaDownloadService),
                "实际演示文件解析后的导入面板；未点击确认导入。");
        });

        var marker = project.GuideNodes.Cast<MapMarkerBase>().Concat(project.Waypoints).FirstOrDefault();
        await Shot("03-marker.png", "攻略节点与标注编辑", () => Show(new MarkerEditorWindow(
            marker ?? throw new InvalidOperationException("演示项目没有标注。"), project), "未保存修改。"));
        await Shot("32-batch-markers.png", "批量编辑普通标注与攻略节点", () =>
        {
            var batch = new BatchMarkerEditorWindow(project.GuideNodes.Take(2).Cast<MapMarkerBase>().Concat(project.Waypoints.Take(1)).ToArray(), project);
            batch.ApplyDescription.IsChecked = true; batch.DescriptionBox.Text = "示例：请核实现场路况与补给。";
            return Show(batch, "仅勾选描述；名称、类型与各点身份保持不变。未保存修改。");
        });
        await Shot("35-snap-branches.png", "重合路段选择去程或回程", () =>
        {
            var loop=new Track {Name="重合路段示例",Segments=[new() {Points=[new() {Latitude=23,Longitude=113},new() {Latitude=23,Longitude=113.01},new() {Latitude=23.01,Longitude=113.01},new() {Latitude=23,Longitude=113.01},new() {Latitude=23,Longitude=113}]}]};
            loop.RecalculateDistances();
            var picker=new SnapBranchWindow(loop,new OverlapSnapService().Find(loop,new(23,113.005)));
            picker.CandidateGrid.SelectedIndex=1;
            return Show(picker,"演示重合路段去程和回程的不同里程；未提交修改。");
        });
        await Shot("04-media.png", "项目图片查看", async () =>
        {
            var owner = project.MediaAttachments.Select(item => item.OwnerId).FirstOrDefault();
            if (owner == Guid.Empty) throw new InvalidOperationException("演示项目没有本地项目图片，未用在线下载代替。");
            var downloader = _externalMediaDownloadService ?? throw new InvalidOperationException("图片服务未初始化。");
            var viewer = new MediaViewerWindow(project, owner, downloader, (_, _) => Task.CompletedTask, allowSaveToProject: false);
            var result = await Show(viewer, "仅显示项目内已有图片；不下载或保存。");
            for (var attempt = 0; attempt < 60 && viewer.Photo.Source is null; attempt++) await Task.Delay(100);
            if (viewer.Photo.Source is null) { viewer.Close(); throw new InvalidOperationException("项目图片尚未成功显示：" + viewer.StatusLabel.Text); }
            return result;
        });
        await Shot("05-map-layers.png", "全局三层地图管理", () => Show(new MapSourceManagerWindow(
            GlobalSettingsService, MapSourceTestService, MapSourceTemplateService)));
        await Shot("06-map-source.png", "多服务器 XYZ 图源", () =>
        {
            var source = GlobalSettingsService.Current.MapSources.FirstOrDefault(item =>
                item.Id == HikingGuide.Infrastructure.Maps.GlobalSettingsService.HumanitarianSourceId)
                ?? throw new InvalidOperationException("缺少内置 Humanitarian 图源，不展示用户的自定义请求头或令牌。");
            return Show(new MapSourceEditorWindow(source.DeepCopy(), MapSourceTemplateService),
                "仅展示内置图源副本，未保存；不展示自定义令牌。");
        });

        await Shot("07-planning.png", "手绘规划与控制点", async () =>
        {
            if (main.IsRoutePlanningActive) T10ManualInvoke(main, "CancelRoutePlan", false);
            main.StartManualPlanCommand.Execute(null);
            for (var attempt = 0; attempt < 30 && !main.IsRoutePlanningActive; attempt++) await Task.Delay(100);
            var planning = T10ManualField<RoutePlanService>(main, "_routePlan");
            var points = track.Segments.SelectMany(item => item.Points).ToArray();
            if (points.Length < 3) throw new InvalidOperationException("演示轨迹点数不足。");
            foreach (var point in new[] { points[0], points[points.Length / 2], points[^1] })
                await (Task)T10ManualInvoke(main, "AddRoutePointAsync", planning, point.Coordinate)!;
            await Task.Delay(250);
            return await Tab(1, "使用演示轨迹坐标创建真实手绘草稿；未完成提交，不修改项目轨迹。");
        });
        if (main.IsRoutePlanningActive) T10ManualInvoke(main, "CancelRoutePlan", false);
        await Shot("08-track-edit.png", "项目内多轨迹编辑模式", async () =>
        {
            main.SelectNavigationNode(new NavigationNode(track.Name ?? "轨迹", track), focusOnMap: false);
            main.EnterTrackEditCommand.Execute(null);
            await Task.Delay(450);
            if (!main.IsTrackEditActive) throw new InvalidOperationException("轨迹编辑模式未进入。");
            var point = JsonSerializer.Deserialize<JsonElement>(await mainWeb.CoreWebView2.ExecuteScriptAsync($"window.hikingGuideT10Test.location('{track.Id:D}', .4)"));
            foreach (var type in new[] { "mousePressed", "mouseReleased" })
                await mainWeb.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
                { type, x = point.GetProperty("x").GetDouble(), y = point.GetProperty("y").GetDouble(), button = "left", buttons = type == "mousePressed" ? 1 : 0, clickCount = 1 }));
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (main.SelectedTrackEditPointId is not null && !main.IsBusy) break;
                await Task.Delay(100);
            }
            return await Tab(0, "只进入编辑并显示句柄，没有拖动或删除轨迹点。");
        });
        if (main.IsTrackEditActive) main.ExitTrackEditCommand.Execute(null);
        await Shot("09-track-color.png", "轨迹颜色选择器", () => Show(new TrackColorWindow(track.DisplayColor ?? "#2877C7")));
        var otherTrack = project.Tracks.First(item => item.Id != track.Id);
        main.AutoConfirmTrackEditForAutomation = true;
        await main.ChangeMainTrackForAutomationAsync(null); await main.WaitForMapRefreshForAutomationAsync();
        await Shot("34-ordinary-role.png", "主攻略转普通轨迹并同步节点", async () =>
        {
            foreach (var node in T10ManualNodes(main.NavigationNodes))
                node.IsExpanded = node.Model is HikingProject or Track || node.Children.Any(c => c.Model is Waypoint);
            return await Tab(0, "通过实际身份转换命令将主线变为普通轨迹；5个攻略节点成为所属普通标注，图片归属同步。演示项目中执行，可撤销。");
        });
        var mixedSources = new TrackMergeSource[] { new(track.Id, false),
            new(otherTrack.Id, false, otherTrack.GetSplitSections()[0].Id) };
        await Shot("30-map-multiselect.png", "地图整轨与拆分分段Ctrl混选", async () =>
        {
            foreach (var target in new[] { (track.Id, 1), (otherTrack.GetSplitSections()[0].Id, 2) })
            {
                var position = JsonSerializer.Deserialize<JsonElement>(await mainWeb.CoreWebView2.ExecuteScriptAsync($"window.hikingGuideT10Test.location('{target.Item1:D}')"));
                foreach (var type in new[] { "mousePressed", "mouseReleased" })
                    await mainWeb.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
                    { type, x = position.GetProperty("x").GetDouble(), y = position.GetProperty("y").GetDouble(), modifiers = target.Item2,
                        button = "left", buttons = type == "mousePressed" ? 1 : 0, clickCount = 1 }));
                await Task.Delay(350);
            }
            for (var attempt = 0; attempt < 30 && main.SelectedSplitSectionIds.Count != 1; attempt++) await Task.Delay(100);
            if (main.SelectedSplitSectionIds.Count != 1) throw new InvalidOperationException("地图真实 Ctrl 混选尚未成功。");
            return await Tab(0, "真实地图鼠标输入：Alt选择整轨、Ctrl加入另一轨迹的一个拆分分段，均高亮；未执行合并或删除。");
        });
        await Shot("10-track-merge.png", "统一合并轨迹面板", () => Show(new TrackMergeWindow(project, false,
            geometrySelection: mixedSources), "真实混选面板，删除原有轨迹默认勾选；未点击创建。"));
        await Shot("31-track-connect.png", "统一连接轨迹面板", () => Show(new TrackMergeWindow(project, true,
            geometrySelection: mixedSources), "真实混选面板及人工直线预览，删除原有轨迹默认勾选；未点击创建。"));
        main.UndoCommand.Execute(null); await main.WaitForMapRefreshForAutomationAsync();
        main.AutoConfirmTrackEditForAutomation = false;
        await Shot("11-brouter.png", "BRouter 设置与数据管理", () => Show(new BRouterSettingsWindow(
            BRouterController ?? throw new InvalidOperationException("BRouter 管理器未初始化。")), "未启动下载或本地导入。"));
        await Shot("12-world-tiles.png", "全球路由数据选择地图", async () =>
        {
            var controller = BRouterController ?? throw new InvalidOperationException("BRouter 管理器未初始化。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var available = await controller.WorldTiles.GetAvailableAsync(timeout.Token);
            var world = new WorldTileWindow(controller, available);
            var result = await Show(world, "读取官方可用目录并打开真实世界页面；未下载 rd5。");
            var ready = await world.RunMapSmokeAsync();
            if (!ready.Ready || !ready.Osm || !ready.Grid) { world.Close(); throw new InvalidOperationException("真实世界页面或瓦片网格未就绪。"); }
            return result;
        });
        await Shot("13-dem.png", "多 DEM 数据库管理", () => Show(new DemDatabaseWindow(DemCatalog), "仅显示已挂载数据库，未重新扫描或改动目录。"));
        await Shot("14-elevation.png", "高程曲线与黄色攻略节点", async () =>
        {
            await Tab(2);
            if (!await window.ElevationAnalysis.RunAutomationGpsAsync(project, track))
                throw new InvalidOperationException("GPS 高程剖面没有可用曲线。");
            return (window, "使用原始 GPS 高程演示剖面交互；不把 GPS 海拔替换为最终攻略的 DEM 基准。");
        });
        await Shot("15-favorites.png", "收藏点单行面板", () => Tab(3));
        await Shot("16-favorites-manager.png", "收藏点管理器", () =>
        {
            window.FavoritesContent.OpenManager();
            var manager = Windows.OfType<Window>().LastOrDefault(item => item != window && item.Title == "收藏点管理器")
                ?? throw new InvalidOperationException("收藏点管理器未打开。");
            return Task.FromResult<(Window, string?)>((manager, "真实收藏点管理器，仅浏览。"));
        });
        await Shot("17-favorite-editor.png", "收藏点编辑", async () =>
        {
            var point = (await Favorites.SearchAsync()).FirstOrDefault() ?? throw new InvalidOperationException("隔离收藏库没有演示点。");
            return await Show(new FavoriteEditorWindow(Favorites, await Favorites.CollectionsAsync(), point,
                await Favorites.GetAsync(point.Id)), "未保存、删除或添加图片。");
        });
        await Shot("18-guide.png", "攻略数据与每日行程", async () =>
        {
            await Tab(0); window.GuideWorkbench.RefreshForAutomation();
            if (window.GuideWorkbench.CalculationTask is { } calculation) await calculation.WaitAsync(TimeSpan.FromSeconds(30));
            return (window, "演示攻略表及每日行程；未修改单元格。");
        });
        try
        {
            var table = window.GuideWorkbench.DailyTable;
            var row = table.Items.Cast<object>().FirstOrDefault() ?? throw new InvalidOperationException("每日行程为空。");
            var column = table.Columns.First(item => item.SortMemberPath == "Departure");
            table.ScrollIntoView(row, column); await T10ManualLayoutAsync(window); table.UpdateLayout();
            var cell = column.GetCellContent(row) ?? throw new InvalidOperationException("出发时间单元格未呈现。");
            var picker = cell as GuideTimePicker ?? FavoritesPanel.Descendants<GuideTimePicker>(cell).FirstOrDefault()
                ?? throw new InvalidOperationException("小时/分钟选择器未生成。");
            picker.Open(); await T10ManualLayoutAsync(window);
            await CaptureT10ElementAsync(picker.PickerPanel, Brushes.White, Path.Combine(directory, "19-time-picker.png"));
            shots.Add(new("screenshots/19-time-picker.png", "小时/分钟时间选择器", true, window.Title,
                "真实每日表单元格打开的弹出组件特写；未点击确定，不提交时间。", null));
            picker.CancelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        catch (Exception error) { shots.Add(new("screenshots/19-time-picker.png", "小时/分钟时间选择器", false, window.Title, null, error.GetBaseException().Message)); }

        await T10ManualDialogAsync("20-plans.png", "行进方案管理", "行进方案管理", () => T10ManualInvoke(window.GuideWorkbench, "ManagePlans"));
        await Shot("21-export.png", "GPX/KML/KMZ 合并导出", () => Show(new MergedExportWindow(project), "真实合并导出选择窗口，不是 Excel 导出结果；未点击导出。"));
        await Shot("22-new-project.png", "新建项目", () => Show(new NewProjectWindow(), "未填写确认，不创建项目。"));
        await Shot("23-track-properties.png", "轨迹名称及属性", () => Show(new ProjectItemEditorWindow(track), "未保存轨迹属性。"));
        await PopupShot("24-context-track.png", "项目树轨迹右键菜单", async () =>
        {
            await Tab(0); main.SelectNavigationNode(new NavigationNode(track.Name ?? "轨迹", track), focusOnMap: false);
            await T10ManualLayoutAsync(window);
            var text = FavoritesPanel.Descendants<TextBlock>(window.ProjectTree).FirstOrDefault(item =>
                item.DataContext is NavigationNode { Model: Track candidate } && candidate.Id == track.Id && item.ContextMenu is not null)
                ?? throw new InvalidOperationException("项目树中的实际轨迹文字尚未呈现。");
            var menu = text.ContextMenu ?? throw new InvalidOperationException("实际轨迹上下文菜单为空。");
            menu.PlacementTarget = text; menu.Placement = PlacementMode.Right;
            menu.IsOpen = true; await T10ManualLayoutAsync(window); return menu;
        }, "实际项目树轨迹右键菜单特写；没有执行任一菜单命令。");
        await PopupShot("25-context-marker.png", "普通标注右键与精确吸附", async () =>
        {
            var ordinary = project.Waypoints.FirstOrDefault() ?? throw new InvalidOperationException("演示项目缺少普通标注。");
            var map = FavoritesPanel.Descendants<MapView>(window).First();
            var message = T10ManualMapMessage("MarkerRightClicked", ordinary.Latitude, ordinary.Longitude,
                waypointId: ordinary.Id);
            T10ManualInvoke(map, "OnRightClicked", main.Map, message);
            await T10ManualLayoutAsync(window); return T10ManualOpenContextMenu();
        }, "由软件实际标注右键处理程序生成的菜单特写；未修改、删除或吸附标注。");
        await PopupShot("26-boundary-hover.png", "每日边界悬浮汇总", async () =>
        {
            await Tab(0); window.GuideWorkbench.RefreshForAutomation(); await T10ManualLayoutAsync(window);
            var table = window.GuideWorkbench.Table;
            var column = table.Columns.First(item => item.SortMemberPath.StartsWith("Plan:", StringComparison.Ordinal));
            var row = table.Items.OfType<GuideWorkbenchControl.Row>().FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item[column.SortMemberPath])) ?? throw new InvalidOperationException("没有已设置的每日边界。");
            table.ScrollIntoView(row, column); await T10ManualLayoutAsync(window); table.UpdateLayout();
            var cell = column.GetCellContent(row) ?? throw new InvalidOperationException("行进方案边界格没有呈现。");
            var button = cell as Button ?? FavoritesPanel.Descendants<Button>(cell).FirstOrDefault()
                ?? throw new InvalidOperationException("行进方案实际按钮没有呈现。");
            var tooltip = button.ToolTip as ToolTip ?? throw new InvalidOperationException("实际每日汇总悬浮框尚未绑定。");
            tooltip.PlacementTarget = button; tooltip.IsOpen = true; await T10ManualLayoutAsync(window); return tooltip;
        }, "实际已设置边界格的 ToolTip 特写；内容来自攻略工作台，不重建或改写汇总文本。");
        await PopupShot("27-weather-context.png", "地图空白处右键与天气入口", async () =>
        {
            var map = FavoritesPanel.Descendants<MapView>(window).First();
            var point = track.StartPoint ?? throw new InvalidOperationException("轨迹缺少起点。");
            T10ManualInvoke(map, "OnRightClicked", main.Map, T10ManualMapMessage("MapRightClicked", point.Latitude, point.Longitude));
            await T10ManualLayoutAsync(window); return T10ManualOpenContextMenu();
        }, "实际地图空白处菜单，显示 windy/meteoblue 入口；未打开外部网页。");
        await Shot("29-about.png", "软件 T1.0 版本与帮助", () => Show(new AboutWindow(), "真实关于窗口及透明 PNG 标志；未打开外部浏览器。"));
        await Shot("33-kmz-split-tree.png", "合并KMZ导入为单父轨迹与拆分分段", async () =>
        {
            var selection = new HikingGuide.Infrastructure.TrackFiles.MergedExportSelection();
            foreach (var source in project.Tracks) selection.TrackIds.Add(source.Id);
            foreach (var point in project.Waypoints) selection.MarkerIds.Add(point.Id);
            var path = Path.Combine(root, "山脊合并线路示例.kmz");
            await new HikingGuide.Infrastructure.TrackFiles.ProjectMergedExportService().ExportAsync(project, selection, TrackFileFormat.Kmz, path, false);
            var files = T10ManualField<ITrackFileService>(main, "_trackFiles");
            var parsed = await new ProjectImportBatch(files).ReadAsync([path], project);
            if (!parsed.IsSuccess || parsed.Tracks.Count != 1) throw new InvalidOperationException("实际演示KMZ没有导入为单父轨迹。");
            var import = new ProjectImportService(); var session = ProjectImportSession.Create(parsed, path, project);
            import.Commit(session, project, true); main.RefreshProjectTreeForAutomation();
            foreach (var node in T10ManualNodes(main.NavigationNodes))
                node.IsExpanded = node.Model is HikingProject || node.Model is Track t && t.Id == parsed.Tracks[0].Id || node.Children.Any(c => c.Model is SplitSection);
            main.SelectNavigationNode(new NavigationNode(parsed.Tracks[0].Name!, parsed.Tracks[0]), focusOnMap: false);
            await main.WaitForMapRefreshForAutomationAsync();
            return await Tab(0, "实际导出并重新导入临时演示KMZ；父轨迹使用文件名，内部轨迹显示为拆分分段。没有使用或修改真实用户项目。");
        });
        await Tab(0);
        var demoDem=DemCatalog.Databases.FirstOrDefault(d=>d.Status==HikingGuide.Infrastructure.Elevation.DemDatabaseStatus.Ready&&d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase))??DemCatalog.Databases.FirstOrDefault(d=>d.Status==HikingGuide.Infrastructure.Elevation.DemDatabaseStatus.Ready);
        GlobalSettingsService.Current.OfflineTerrain=GlobalSettingsService.Current.OfflineTerrain with{DemDatabaseId=demoDem?.Id,ContoursVisible=true,HillshadeVisible=true};
        foreach(var tab in new[]{(0,"81-online-library.png","在线图源库"),(1,"82-contours.png","等高线生成器"),(2,"83-osm-roads.png","OSM路网"),(3,"84-offline-download.png","下载离线地图")})
            await Shot(tab.Item2,tab.Item3,async()=>{var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};manager.Show();T10ManualField<TabControl>(manager,"LibraryTabs").SelectedIndex=tab.Item1;await T10ManualLayoutAsync(manager);return(manager,"当前程序实际面板，未提交设置或开始下载。");});
        await Shot("85-terrain-settings.png","3D地图设置",()=>Show(new Terrain3DWindow(main.Map),"当前程序实际设置面板，未提交设置。"));
        if(demoDem is not null)
        {
            try
            {
                GlobalSettingsService.Current.Terrain3D=new(){Enabled=true,TerrainDemDatabaseId=demoDem.Id,Exaggeration=1.5};
                await main.Map.ApplyGlobalMapLayersAsync(true);
                await mainWeb.CoreWebView2.ExecuteScriptAsync("window.hikingGuide3DTest.at([113.32,24.42],15);window.hikingGuide3DTest.camera(45,25)");
                var effective=false;
                for(var i=0;i<240;i++){var state=JsonSerializer.Deserialize<JsonElement>(await mainWeb.CoreWebView2.ExecuteScriptAsync("window.hikingGuide3DTest.inspect()"));if(state.GetProperty("effective").GetBoolean()){effective=true;break;}await Task.Delay(250);}
                if(!effective)throw new InvalidOperationException("真实DEM的3D地形未完成加载");
                await Task.Delay(3000);
                await using(var image=File.Create(Path.Combine(directory,"87-map-navigation.png")))await mainWeb.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,image);
                shots.Add(new("screenshots/87-map-navigation.png","3D地形与右下角导航",true,window.Title,"真实DEM与当前地图，不修改原高程文件。",null));
            }catch(Exception e){shots.Add(new("screenshots/87-map-navigation.png","3D地形与右下角导航",false,window.Title,null,e.Message));}
        }
        await File.WriteAllTextAsync(Path.Combine(root, "screenshots-manifest.json"), JsonSerializer.Serialize(new
        {
            SoftwareVersion = HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion,
            CapturedAt = DateTimeOffset.Now, Project = project.Name,
            CaptureMethod = "真实 WPF 客户端内容 + 原生 WebView2 PNG 预览按实际位置合成；不伪造 UI，不绘制标题栏。",
            SuccessCount = shots.Count(item => item.Success), FailureCount = shots.Count(item => !item.Success), Screenshots = shots
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        await File.WriteAllTextAsync(Path.Combine(root, "截图记录.md"), "# T1.0 真实软件截图记录\n\n" +
            string.Join("\n\n", shots.Select(item => item.Success
                ? $"- [{item.Module}]({item.File})：实际窗口标题“{item.WindowTitle}”。{item.Note}"
                : $"- {item.Module}：未生成截图。原因：{item.Error}")));

        async Task T10ManualDialogAsync(string file, string module, string title, Action open)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) }; var attempts = 0;
            timer.Tick += async (_, _) =>
            {
                var dialog = Windows.OfType<Window>().LastOrDefault(item => item != window && item.Title == title);
                if (dialog is null) { if (++attempts < 30) return; timer.Stop(); completion.TrySetException(new TimeoutException("真实管理窗口没有打开。")); return; }
                timer.Stop();
                try { await T10ManualLayoutAsync(dialog); await CaptureT10ClientAsync(dialog, Path.Combine(directory, file)); shots.Add(new("screenshots/" + file, module, true, dialog.Title, "只打开并关闭，不按增加、删除或确认按钮。", null)); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dialog.Close(); }
            };
            try { timer.Start(); open(); await completion.Task; }
            catch (Exception error) { shots.Add(new("screenshots/" + file, module, false, null, null, error.GetBaseException().Message)); }
            finally { timer.Stop(); }
        }
        async Task PopupShot(string file, string module, Func<Task<FrameworkElement>> open, string note)
        {
            FrameworkElement? element = null;
            try
            {
                element = await open(); await Task.Delay(100); element.UpdateLayout();
                await CaptureT10ElementAsync(element, Brushes.White, Path.Combine(directory, file));
                shots.Add(new("screenshots/" + file, module, true, window.Title, note, null));
            }
            catch (Exception error) { shots.Add(new("screenshots/" + file, module, false, window.Title, null, error.GetBaseException().Message)); }
            finally { if (element is ContextMenu menu) menu.IsOpen = false; if (element is ToolTip tooltip) tooltip.IsOpen = false; }
        }
        MapEventMessage T10ManualMapMessage(string type, double latitude, double longitude, Guid? waypointId = null) =>
            MapProtocol.TryParseEvent(JsonSerializer.Serialize(new
            {
                protocolVersion = MapProtocol.Version, type, projectId = project.Id,
                latitude, longitude, waypointId, screenX = 110, screenY = 80
            })) ?? throw new InvalidOperationException("无法生成实际地图菜单消息。");
    }

    private static IEnumerable<NavigationNode> T10ManualNodes(IEnumerable<NavigationNode> nodes)
    {
        foreach (var node in nodes) { yield return node; foreach (var child in T10ManualNodes(node.Children)) yield return child; }
    }
    private static T T10ManualField<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
            ?? throw new InvalidOperationException("截图准备缺少实际运行对象：" + name));
    private static object? T10ManualInvoke(object target, string name, params object?[] args) =>
        (target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("实际操作不存在：" + name)).Invoke(target, args);
    private static ContextMenu T10ManualOpenContextMenu() => PresentationSource.CurrentSources.OfType<HwndSource>()
        .Where(source => source.RootVisual is not null)
        .SelectMany(source => FavoritesPanel.Descendants<ContextMenu>(source.RootVisual!))
        .LastOrDefault(menu => menu.IsOpen) ?? throw new InvalidOperationException("实际右键菜单未打开。");
    private static async Task T10ManualLayoutAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        await Task.Delay(180);
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
    }

    private static async Task CaptureT10ClientAsync(Window window, string path)
    {
        var content = window.Content as FrameworkElement ?? throw new InvalidOperationException("窗口没有可截图的客户端内容。");
        await CaptureT10ElementAsync(content, window.Background ?? Brushes.White, path);
    }

    private static async Task CaptureT10ElementAsync(FrameworkElement content, Brush background, string path)
    {
        content.UpdateLayout();
        var offset = VisualTreeHelper.GetOffset(content);
        var left = Math.Max(0, offset.X); var top = Math.Max(0, offset.Y);
        var width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth + left + Math.Max(0, content.Margin.Right)));
        var height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight + top + Math.Max(0, content.Margin.Bottom)));
        if (width < 10 || height < 10) throw new InvalidOperationException("实际 UI 尚未完成布局。");
        // 保留实际根布局偏移及边距，画布覆盖整个客户端，不能截掉右侧与底部按钮。
        var surface = new DrawingVisual();
        using (var drawing = surface.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(content)
            { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height), Stretch = Stretch.Fill },
                null, new Rect(0, 0, width, height));
        var wpf = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); wpf.Render(surface);
        var webImages = new List<(BitmapImage Image, Rect Bounds)>();
        foreach (var web in FavoritesPanel.Descendants<WebView2>(content).Where(item => item.IsVisible && item.ActualWidth > 1 && item.ActualHeight > 1))
        {
            if (web.CoreWebView2 is null) throw new InvalidOperationException("真实 WebView2 尚未初始化，不能用空白图片冒充地图截图。");
            using var stream = new MemoryStream();
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Position = 0;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            var position = web.TranslatePoint(new Point(), content);
            webImages.Add((image, new Rect(new Point(position.X + left, position.Y + top), new Size(web.ActualWidth, web.ActualHeight))));
        }
        var composite = new DrawingVisual();
        using (var drawing = composite.RenderOpen())
        {
            drawing.DrawRectangle(background, null, new Rect(0, 0, width, height));
            drawing.DrawImage(wpf, new Rect(0, 0, width, height));
            foreach (var (image, bounds) in webImages) drawing.DrawImage(image, bounds);
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); result.Render(composite);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(result));
        await using var output = File.Create(path); encoder.Save(output);
    }
}

