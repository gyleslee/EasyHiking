using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Favorites;
using HikingGuide.Core.Geometry;
using HikingGuide.Infrastructure.Favorites;
using Microsoft.Web.WebView2.Wpf;

namespace HikingGuide.App;
public partial class App
{
    private async Task<bool> RunP92SmokeAsync(MainWindow window, MainViewModel main, string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var checks = new Dictionary<string, bool>(); var demMoves = new List<object>(); string? error = null;
        FavoritesPanel.MouseProbe.GetCursorPos(out var originalCursor); var originalWindow = FavoritesPanel.MouseProbe.GetForegroundWindow(); var oldTopmost = window.Topmost;
        try
        {
            if (!Favorites.Paths.FavoritesRoot.StartsWith("F:\\Codex\\Temp\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("必须使用隔离验收库。");
            var now = DateTimeOffset.UtcNow; var folder = await Favorites.SaveCollectionAsync(new(Guid.NewGuid(), "P9-2摄影点", 1, now, now));
            using var imageStream = GetResourceStream(new Uri("pack://application:,,,/Assets/AppLogo.png"))!.Stream; using var imageMemory = new MemoryStream(); await imageStream.CopyToAsync(imageMemory);
            var image = await Task.Run(() => HikingGuide.App.Services.FavoriteImageService.FromBytes(imageMemory.ToArray(), "原图.png"));
            var points = Enumerable.Range(0, 20).Select(i => new FavoritePoint { Name = $"地图收藏点{i + 1:D2}", Description = "信息牌中文说明\n第二行 & < >", Latitude = 23.13 + i / 5 * .002, Longitude = 113.3 + i % 5 * .002,
                CollectionId = folder.Id, ElevationMeters = 555, ElevationSourceKind = FavoriteElevationSourceKind.CopiedGps, SourceReference = "https://example.com/guide" }).ToArray();
            foreach (var p in points) await Favorites.SaveAsync(p, p == points[0] ? [image] : []);
            window.Width = 1400; window.Height = 1000; window.Topmost = true; window.Activate(); FavoritesPanel.MouseProbe.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle); window.ShowFavoritesForAutomation(); await window.FavoritesContent.RefreshAsync();
            for (var i = 0; i < 100 && !main.Map.IsReady; i++) await Task.Delay(100);
            var web = FavoritesPanel.Descendants<WebView2>(window).First(w => w.Source?.Host == "hikingguide.local");
            async Task<JsonElement> Script(string code) => JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            Task<JsonElement> State() => Script("window.hikingGuideFavoriteTest.inspect()");
            async Task Wait(Func<JsonElement, bool> predicate) { for (var i = 0; i < 80; i++) { if (predicate(await State())) return; await Task.Delay(50); } throw new TimeoutException("地图状态未按预期更新：" + (await State()).GetRawText()); }
            void Mouse(Point point, uint down = 0, uint up = 0) { var screen = web.PointToScreen(point); FavoritesPanel.MouseProbe.SetCursorPos((int)screen.X, (int)screen.Y); if (down != 0) FavoritesPanel.MouseProbe.mouse_event(down, 0, 0, 0, UIntPtr.Zero); if (up != 0) FavoritesPanel.MouseProbe.mouse_event(up, 0, 0, 0, UIntPtr.Zero); }
            async Task<Point> Marker(Guid id) { await Script($"window.hikingGuideFavoriteTest.center('{id:D}',16)"); await Task.Delay(150); var value = await Script($"window.hikingGuideFavoriteTest.point('{id:D}')"); return new(value.GetProperty("x").GetDouble(), value.GetProperty("y").GetDouble()); }
            async Task Hover(Guid id) { Mouse(new Point(10, 10)); await Task.Delay(160); var point = await Marker(id); Mouse(point); for (var attempt = 0; attempt < 20; attempt++) { var s = await State(); if (s.GetProperty("hoverId").GetString() == id.ToString() && s.GetProperty("popupCount").GetInt32() == 1) return; await Task.Delay(100); Mouse(new Point(point.X + attempt % 2, point.Y)); } await Wait(s => s.GetProperty("hoverId").GetString() == id.ToString() && s.GetProperty("popupCount").GetInt32() == 1); }
            window.UpdateLayout();
            var toolbar = FavoritesPanel.Descendants<Grid>(window.FavoritesContent).First(g => g.ColumnDefinitions.Count == 4);
            checks["SingleToolbarCorrectOrder"] = toolbar.RowDefinitions.Count == 0 && toolbar.Children[0] is StackPanel collectionHost && FavoritesPanel.Descendants<ComboBox>(collectionHost).Count() == 1 && FavoritesPanel.Descendants<TextBlock>(collectionHost).Any(t => t.Text == "收藏夹：") && toolbar.Children[1] is StackPanel searchHost && FavoritesPanel.Descendants<TextBlock>(searchHost).Any(t => t.Text == "搜索🔍") && FavoritesPanel.Descendants<TextBox>(searchHost).Single().ActualWidth == 250 && toolbar.Children[2] is Button && toolbar.Children[3] is CheckBox && toolbar.ActualHeight < 45;
            var list = FavoritesPanel.Descendants<ListBox>(window.FavoritesContent).First();
            var collectionBox = FavoritesPanel.Descendants<ComboBox>(toolbar).Single();
            collectionBox.IsDropDownOpen = true; await Task.Delay(100); collectionBox.UpdateLayout();
            var collectionItem = (ComboBoxItem)collectionBox.ItemContainerGenerator.ContainerFromIndex(0);
            checks["CollectionTextSizeAndAlignment"] = collectionBox.FontSize == 14 && collectionBox.ActualHeight >= 30 && collectionBox.HorizontalContentAlignment == HorizontalAlignment.Left && collectionBox.VerticalContentAlignment == VerticalAlignment.Center && collectionItem.FontSize == 14 && collectionItem.HorizontalContentAlignment == HorizontalAlignment.Left && collectionItem.VerticalContentAlignment == VerticalAlignment.Center;
            collectionBox.IsDropDownOpen = false; System.Windows.Input.Mouse.Capture(null); await Task.Delay(150); window.Activate(); web.Focus();
            list.SelectedItem = list.Items.Cast<FavoritePoint>().First(p => p.Id == points[0].Id); await Task.Delay(600);
            await Wait(s => s.GetProperty("selectedId").GetString() == points[0].Id.ToString()); checks["PanelSelectsGoldAndFlyTo"] = FavoriteSelection.SelectedFavoritePointId == points[0].Id;
            list.SelectedItem = list.Items.Cast<FavoritePoint>().First(p => p.Id == points[1].Id); await Task.Delay(600);
            var state = await State(); checks["OnlyOneGoldAndGlow"] = state.GetProperty("goldIds").GetArrayLength() == 1 && state.GetProperty("goldImage").GetBoolean() && state.GetProperty("glowLayer").GetBoolean();
            var clickPoint = await Marker(points[2].Id); web.Focus(); await Task.Delay(100); Mouse(new(clickPoint.X - 10, clickPoint.Y)); await Task.Delay(50); Mouse(clickPoint, 2, 4); await Wait(s => s.GetProperty("selectedId").GetString() == points[2].Id.ToString());
            checks["MapClickSelectsPanel"] = FavoriteSelection.SelectedFavoritePointId == points[2].Id && list.SelectedItems.Cast<FavoritePoint>().Any(p => p.Id == points[2].Id);
            var oldCoordinate = (await Favorites.GetAsync(points[2].Id))!.Point; checks["ClickDoesNotMove"] = oldCoordinate.Latitude == points[2].Latitude && oldCoordinate.Longitude == points[2].Longitude;
            await Hover(points[0].Id); var popup = await Script("window.hikingGuideFavoriteTest.popupPoint()"); demMoves.Add(new { Case = "Hover坐标诊断", Card = popup, WebWidth = web.ActualWidth, WebHeight = web.ActualHeight, State = await State() }); Mouse(new(popup.GetProperty("x").GetDouble(), popup.GetProperty("y").GetDouble())); await Task.Delay(250);
            state = await State(); checks["MarkerToCardKeepsHover"] = state.GetProperty("popupCount").GetInt32() == 1;
            checks["HoverDetailsAndThumbnail"] = state.GetProperty("popupText").GetString()!.Contains("P9-2摄影点") && state.GetProperty("popupText").GetString()!.Contains("https://example.com/guide") && (await Script("window.hikingGuideFavoriteTest.popupImage()")).GetBoolean();
            CaptureWindowPng(window, Path.ChangeExtension(output, ".panel.png")); await main.Map.CapturePreviewAsync(Path.ChangeExtension(output, ".hover.png"));
            Mouse(new Point(10, 10)); await Task.Delay(180); checks["LeaveCardCloses"] = (await State()).GetProperty("popupCount").GetInt32() == 0;
            var maxPopups = 0; var hoverOk = true;
            foreach (var p in points) { await Hover(p.Id); maxPopups = Math.Max(maxPopups, (await State()).GetProperty("popupCount").GetInt32()); Mouse(new Point(10, 10)); await Task.Delay(180); hoverOk &= (await State()).GetProperty("popupCount").GetInt32() == 0; }
            checks["TwentyMapHoverCyclesSinglePopup"] = hoverOk && maxPopups == 1;
            await Hover(points[0].Id); await Script("window.hikingGuideFavoriteTest.pan()"); await Task.Delay(200); checks["PanClosesHover"] = (await State()).GetProperty("popupCount").GetInt32() == 0;
            await Hover(points[0].Id); var zoomBefore = await State(); await Script("window.hikingGuideFavoriteTest.zoom()"); await Task.Delay(200); var zoomAfter = await State(); checks["ZoomClosesHover"] = zoomAfter.GetProperty("popupCount").GetInt32() == 0; demMoves.Add(new { Case = "缩放关闭诊断", Before = zoomBefore, After = zoomAfter });
            await Hover(points[0].Id); Mouse(await Marker(points[0].Id), 8, 16); await Task.Delay(150);
            var mapView = FavoritesPanel.Descendants<MapView>(window).First(); var menu = mapView.ActiveFavoriteMenu;
            checks["RightClickSelectsAndHasEditor"] = FavoriteSelection.SelectedFavoritePointId == points[0].Id && menu?.Items.OfType<MenuItem>().Any(i => Equals(i.Header, "编辑收藏点…")) == true && (await State()).GetProperty("popupCount").GetInt32() == 0;
            var editMenu = menu!.Items.OfType<MenuItem>().First(i => Equals(i.Header, "编辑收藏点…")); menu.IsOpen = false;
            _ = Dispatcher.BeginInvoke(() => editMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            FavoriteEditorWindow? editor = null; for (var i = 0; i < 40 && editor is null; i++) { await Task.Delay(50); editor = Windows.OfType<FavoriteEditorWindow>().FirstOrDefault(); }
            if (editor is null) throw new InvalidOperationException("右键编辑未打开现有收藏编辑器。"); await editor.RunP92EditAsync("P9-2地图编辑新名称", "P9-2地图编辑新说明"); await Task.Delay(350);
            checks["EditorChangesMapNameAndHover"] = (await State()).GetProperty("names").EnumerateArray().Any(n => n.GetString() == "P9-2地图编辑新名称");
            await Hover(points[0].Id); checks["EditorChangesMapNameAndHover"] &= (await State()).GetProperty("popupText").GetString()!.Contains("P9-2地图编辑新说明");
            Mouse(new Point(10, 10)); await Task.Delay(180);
            var commitsBeforeDrag = FavoriteMover.CommittedMoveCount;
            var beforeDrag = (await Favorites.GetAsync(points[0].Id))!.Point; var start = await Marker(points[0].Id); Mouse(start, 2); Mouse(new(start.X + 50, start.Y + 35)); await Task.Delay(100);
            state = await State(); checks["DragPreviewDisablesPanWithoutWriting"] = state.GetProperty("dragActive").GetBoolean() && !state.GetProperty("panEnabled").GetBoolean() && (await Favorites.GetAsync(points[0].Id))!.Point == beforeDrag;
            Mouse(new(start.X + 50, start.Y + 35), 0, 4); for (var i = 0; i < 150 && (await Favorites.GetAsync(points[0].Id))!.Point == beforeDrag; i++) await Task.Delay(50); await Task.Delay(300);
            var moved = (await Favorites.GetAsync(points[0].Id))!.Point;
            checks["DragCommitsCoordinateAndHeight"] = moved.Latitude != beforeDrag.Latitude && moved.Longitude != beforeDrag.Longitude && moved.ElevationSourceKind == FavoriteElevationSourceKind.CopiedDem;
            checks["DragRestoresPan"] = (await State()).GetProperty("panEnabled").GetBoolean();
            checks["ExactlyOneBusinessCommitForDrag"] = FavoriteMover.CommittedMoveCount == commitsBeforeDrag + 1;
            demMoves.Add(new { Case = "实体鼠标拖动", Old = beforeDrag, New = moved });
            start = await Marker(points[0].Id); Mouse(start, 2); Mouse(new(start.X + 35, start.Y + 20)); await Task.Delay(100);
            FavoritesPanel.MouseProbe.keybd_event(0x1B, 0, 0, UIntPtr.Zero); FavoritesPanel.MouseProbe.keybd_event(0x1B, 0, 2, UIntPtr.Zero); await Task.Delay(150); Mouse(new(start.X + 35, start.Y + 20), 0, 4); await Task.Delay(250);
            checks["EscCancelsMoveAndRestoresPan"] = (await Favorites.GetAsync(points[0].Id))!.Point == moved && (await State()).GetProperty("panEnabled").GetBoolean();
            var fathom = DemCatalog.Databases.First(d => d.Name.Contains("Fathom", StringComparison.OrdinalIgnoreCase));
            foreach (var (label, oldCoord, newCoord) in new[] { ("A覆盖内部", new GeoCoordinate(23.13, 113.3), new GeoCoordinate(23.14, 113.31)), ("B跨HGT瓦片", new GeoCoordinate(23.999, 113.3), new GeoCoordinate(24.001, 113.3)), ("C无覆盖", new GeoCoordinate(23.13, 113.3), new GeoCoordinate(0, -150)) })
            {
                var oldHeight = await Task.Run(() => DemProvider!.SampleDetailed(fathom.Id, oldCoord).ElevationMeters);
                var p = new FavoritePoint { Name = label, Latitude = oldCoord.Latitude, Longitude = oldCoord.Longitude, ElevationMeters = oldHeight, ElevationSourceKind = FavoriteElevationSourceKind.CopiedDem, ElevationSourceReference = fathom.Id + "|" + fathom.Name };
                await Favorites.SaveAsync(p); var result = await FavoriteMover.MoveAsync(p.Id, newCoord); var expected = await Task.Run(() => DemProvider!.SampleDetailed(fathom.Id, newCoord).ElevationMeters);
                checks[label] = result.Point.ElevationMeters == expected && (label == "C无覆盖" ? result.Point.ElevationMeters is null : result.Point.ElevationSourceReference!.StartsWith(fathom.Id.ToString()) && result.Point.ElevationMeters.HasValue && oldHeight.HasValue);
                demMoves.Add(new { Case = label, OldCoordinate = oldCoord, NewCoordinate = newCoord, OldElevation = oldHeight, NewElevation = result.Point.ElevationMeters, result.Point.ElevationSourceReference });
            }
            NotifyFavoritesChanged(); await Task.Delay(300); var manager = new FavoritesPanel(true); var managerWindow = new Window { Owner = window, Title = "P9-2管理器同步验收", Width = 1050, Height = 650, Content = manager }; managerWindow.Show(); await manager.RefreshAsync();
            var rows = FavoritesPanel.Descendants<DataGrid>(manager).First().Items.Cast<FavoritesPanel.FavoriteRow>(); checks["ManagerShowsMovedCoordinateAndHeight"] = rows.Any(r => r.Point.Id == moved.Id && r.Latitude == moved.Latitude && r.Elevation == moved.ElevationMeters); managerWindow.Close();
            var reopen = new FavoritesRepository(new FavoritesPathProvider(Path.GetDirectoryName(Favorites.Paths.FavoritesRoot)!)); checks["ReopenRepositoryKeepsMove"] = (await reopen.GetAsync(moved.Id))!.Point == moved;
            var dense = (await Favorites.SearchAsync()).Take(10).Select(p => p with { Latitude = 23.13, Longitude = 113.3 }).ToArray(); await main.Map.UpdateFavoritesAsync(dense, true); await Marker(dense[0].Id); state = await State();
            checks["DenseNamesAllExistBelowStars"] = state.GetProperty("labels").GetInt32() == 10 && state.GetProperty("labelImages").GetInt32() == 10 && state.GetProperty("anchor").GetString() == "top" && state.GetProperty("overlap").GetBoolean();
            var selected = FavoriteSelection.SelectedFavoritePointId; await main.Map.UpdateFavoritesAsync(dense, false); state = await State(); checks["HideAllFavoriteLayersKeepsSelection"] = state.GetProperty("layerVisibility").EnumerateArray().All(v => v.GetString() == "none") && FavoriteSelection.SelectedFavoritePointId == selected;
            await main.Map.UpdateFavoritesAsync(await Favorites.SearchAsync(), true); await main.Map.CapturePreviewAsync(Path.ChangeExtension(output, ".map.png"));
            var trackRegression = main.RunP91SelectionSmoke(); checks["P91TrackSelectionRegression"] = trackRegression.Values.OfType<bool>().All(v => v);
            var project = main.MapContextProject; var clean = project is not null && !project.IsDirty;
            FavoriteSelection.SelectFavorite(moved); await FavoriteMover.MoveAsync(moved.Id, new GeoCoordinate(moved.Latitude, moved.Longitude));
            checks["ProjectsRemainClean"] = clean && !project!.IsDirty;
        }
        catch (Exception exception) { error = exception.ToString(); CaptureWindowPng(window, Path.ChangeExtension(output, ".error.png")); await main.Map.CapturePreviewAsync(Path.ChangeExtension(output, ".error-map.png")); }
        finally { window.Topmost = oldTopmost; FavoritesPanel.MouseProbe.SetCursorPos(originalCursor.X, originalCursor.Y); if (originalWindow != IntPtr.Zero) FavoritesPanel.MouseProbe.SetForegroundWindow(originalWindow); }
        var passed = error is null && checks.Count >= 20 && checks.Values.All(v => v);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Passed = passed, Error = error, Checks = checks, DemMoves = demMoves }, new JsonSerializerOptions { WriteIndented = true })); return passed;
    }
    private async Task<bool> RunP92ReopenAsync(string previousReport)
    {
        if (!Favorites.Paths.FavoritesRoot.StartsWith("F:\\Codex\\Temp\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("必须使用隔离验收库。");
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(previousReport));
        var expected = report.RootElement.GetProperty("DemMoves").EnumerateArray().First(e => e.TryGetProperty("New", out _)).GetProperty("New").Deserialize<FavoritePoint>()!;
        var actual = (await Favorites.GetAsync(expected.Id))?.Point;
        var passed = actual is not null && actual.Latitude == expected.Latitude && actual.Longitude == expected.Longitude && actual.ElevationMeters == expected.ElevationMeters && actual.ElevationSourceReference == expected.ElevationSourceReference;
        await File.WriteAllTextAsync(Path.ChangeExtension(previousReport, ".reopen.json"), JsonSerializer.Serialize(new { Passed = passed, expected.Latitude, expected.Longitude, expected.ElevationMeters, Actual = actual }, new JsonSerializerOptions { WriteIndented = true })); return passed;
    }
}
