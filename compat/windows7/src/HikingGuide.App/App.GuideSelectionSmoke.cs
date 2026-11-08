using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.App;

public partial class App
{
    private async Task<bool> RunGuideSelectionSmokeAsync(MainWindow window, MainViewModel main, string output)
    {
        var checks = new Dictionary<string, bool>(); string? error = null;
        try
        {
            if (!Favorites.Paths.FavoritesRoot.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("表格选中测试必须使用隔离数据目录。");
            var folder = Path.GetDirectoryName(Path.GetFullPath(output))!; Directory.CreateDirectory(folder);
            var track = new Track { Name = "选中可读性测试", Segments = [new() { Points = [
                new() { Latitude = 23, Longitude = 113, SourceElevation = 100 },
                new() { Latitude = 23.001, Longitude = 113, SourceElevation = 110 },
                new() { Latitude = 23.002, Longitude = 113, SourceElevation = 105 }] }] };
            track.RecalculateDistances(); var project = new HikingProject { Name = "表格选中验收副本" };
            project.AddTrack(track); project.MainGuideTrackId = track.Id;
            var structure = new GuideStructureService();
            foreach (var (point, index) in track.Segments[0].Points.Select((p, i) => (p, i)))
            { var node = new GuideNode { Name = "攻略节点" + (index + 1), TrackId = track.Id, OriginalCoordinate = new(point.Latitude, point.Longitude) }; structure.SnapNode(node, track); project.GuideNodes.Add(node); }
            GuideStructureService.ReplaceSegments(project, structure.RebuildSegments(project));
            project.GuideData.Settings = project.GuideData.Settings with { ElevationSourceKind = GuideElevationSourceKind.Gps };
            var path = Path.Combine(folder, "选中样式.hikeproj"); await new ProjectFileService().SaveAsync(project, path, false);
            window.Width = 1550; window.Height = 1000; window.BottomPanelRow.Height = new GridLength(450);
            await main.OpenForAutomationAsync(path); window.GuideWorkbench.SelectProject(main.MapContextProject);
            await window.GuideWorkbench.CalculateAsync();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            var table = window.GuideWorkbench.Table;
            bool IsBlack(Brush brush) => brush is SolidColorBrush b && b.Color == Colors.Black;
            bool IsWhite(Brush brush) => brush is SolidColorBrush b && b.Color == Colors.White;
            async Task<DataGridCell> Cell(object row, string key)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                row = table.Items.OfType<GuideWorkbenchControl.Row>().First(r => r.Id == ((GuideWorkbenchControl.Row)row).Id);
                var column = table.Columns.First(c => c.SortMemberPath == key);
                table.ScrollIntoView(row, column); table.SelectedItem = row; table.CurrentCell = new(row, column); table.Focus();
                await Dispatcher.InvokeAsync(table.UpdateLayout, DispatcherPriority.Loaded);
                await Dispatcher.InvokeAsync(table.UpdateLayout, DispatcherPriority.Render);
                DependencyObject? cell = column.GetCellContent(row);
                while (cell is not null && cell is not DataGridCell) cell = VisualTreeHelper.GetParent(cell);
                return cell as DataGridCell ?? throw new InvalidOperationException("测试单元格尚未生成。");
            }
            foreach (var index in Enumerable.Range(0, 3))
            {
                var row = table.Items[index];
                foreach (var key in new[] { "GuideNodeName", "Elevation", "ActualArrival", "PlannedDuration" })
                {
                    var cell = await Cell(row, key);
                    checks[$"第{index + 1}行{key}深色文字浅背景蓝边框"] = cell.IsSelected && IsBlack(cell.Foreground) && IsWhite(cell.Background) && cell.BorderBrush is SolidColorBrush border && border.Color == Color.FromRgb(0, 120, 215);
                    var text = FavoritesPanel.Descendants<TextBlock>(cell).FirstOrDefault();
                    if (text is not null) checks[$"第{index + 1}行{key}文字清晰"] = IsBlack(text.Foreground);
                }
            }
            var middle = table.Items[1]; var selected = await Cell(middle, "Elevation");
            window.GuideWorkbench.DailyTable.Focus(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            checks["失去表格焦点仍为深色文字"] = IsBlack(selected.Foreground) && IsWhite(selected.Background);
            var editable = await Cell(middle, "PlannedDuration"); editable.Focus(); table.BeginEdit();
            await Dispatcher.InvokeAsync(table.UpdateLayout, DispatcherPriority.Render);
            var editor = FavoritesPanel.Descendants<TextBox>(editable).FirstOrDefault();
            checks["选中编辑器文字可见"] = editor is not null && IsBlack(editor.Foreground);
            table.CancelEdit(); table.CancelEdit(DataGridEditingUnit.Row);
            checks["计划用时编辑仍可提交"] = await window.GuideWorkbench.EditCellForAutomationAsync(1, "PlannedDuration", "1.25");
            var actual = main.MapContextProject!; var nodes = GuideDataReconciler.OrderedNodes(actual);
            checks["编辑值不受样式改变影响"] = actual.GuideData.Manual.Segments[new GuideSegmentKey(track.Id, nodes[0].Id, nodes[1].Id).StorageKey].PlannedDurationHours == 1.25;
            await Cell(table.Items[1], "GuideNodeName");
            CaptureWindowPng(window, Path.ChangeExtension(output, ".png"));
            await main.FlushGuideAutoSaveAsync();
        }
        catch (Exception exception) { error = exception.ToString(); }
        var success = error is null && checks.Count > 0 && checks.Values.All(v => v);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Success = success, Checks = checks, Error = error }, new JsonSerializerOptions { WriteIndented = true }));
        return success;
    }
}
