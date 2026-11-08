using System.ComponentModel;
using System.Windows;
using HikingGuide.Core.Models;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Infrastructure.TrackFiles;
using HikingGuide.App.ViewModels;

namespace HikingGuide.App;

public partial class MergedExportWindow : Window
{
    private readonly HikingProject _project;
    private readonly List<TrackRow> _tracks;
    private readonly List<MarkerRow> _markers;
    public MergedExportSelection Selection { get; } = new();
    public TrackFileFormat Format => FormatBox.SelectedIndex switch
    {
        0 => TrackFileFormat.Gpx,
        1 => TrackFileFormat.Kml,
        _ => TrackFileFormat.Kmz
    };

    public MergedExportWindow(HikingProject project)
    {
        InitializeComponent();
        _project = project;
        _tracks = project.Tracks.Select(item => new TrackRow(item)).ToList();
        _markers = project.Waypoints.Select(item => new MarkerRow(project, item, false))
            .Concat(project.GuideNodes.Select(item => new MarkerRow(project, item, true))).ToList();
        TrackGrid.ItemsSource = _tracks;
        MarkerGrid.ItemsSource = _markers;
    }

    private void SelectAllTracks(object sender, RoutedEventArgs e) { foreach (var row in _tracks) row.Selected = true; }
    private void InvertTracks(object sender, RoutedEventArgs e) { foreach (var row in _tracks) row.Selected = !row.Selected; }
    private void SelectAllMarkers(object sender, RoutedEventArgs e) { foreach (var row in _markers) row.Selected = true; }
    private void InvertMarkers(object sender, RoutedEventArgs e) { foreach (var row in _markers) row.Selected = !row.Selected; }

    private void ConfirmClicked(object sender, RoutedEventArgs e)
    {
        TrackGrid.CommitEdit(); MarkerGrid.CommitEdit();
        foreach (var row in _tracks.Where(item => item.Selected)) Selection.TrackIds.Add(row.Id);
        foreach (var row in _markers.Where(item => item.Selected)) Selection.MarkerIds.Add(row.Id);
        if (Selection.TrackIds.Count + Selection.MarkerIds.Count == 0)
        {
            MessageBox.Show(this, "请至少选择一条轨迹或一个标注。", "无法导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_project.GuideNodes.Any(node => Selection.MarkerIds.Contains(node.Id) &&
                node.TrackId is Guid id && !Selection.TrackIds.Contains(id)) &&
            MessageBox.Show(this,
                "已选择的部分攻略节点所属轨迹未包含在本次导出中，因此目标文件中无法完整恢复其攻略轨迹关系。仍要继续吗？",
                "轨迹关系提示", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        DialogResult = true;
    }

    private abstract class SelectableRow : INotifyPropertyChanged
    {
        private bool _selected = true;
        public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private sealed class TrackRow(Track track) : SelectableRow
    {
        public Guid Id => track.Id;
        public string Name => track.Name ?? "未命名轨迹";
        public int SegmentCount => track.Segments.Count;
        public int PointCount => track.GetPointCount();
    }
    private sealed class MarkerRow(HikingProject project, MapMarkerBase marker, bool isGuide) : SelectableRow
    {
        public Guid Id => marker.Id;
        public string Name => marker.Name ?? "未命名标注";
        public string TypeName => MarkerTypeNames.Get(marker.Type);
        public string Identity => isGuide ? "攻略" : "普通";
        public string TrackName => marker is GuideNode { TrackId: Guid id }
            ? project.Tracks.FirstOrDefault(item => item.Id == id)?.Name ?? "未找到" : "—";
        public int InternalCount => project.MediaAttachments.Count(item => item.OwnerId == marker.Id);
        public int ExternalCount => project.ExternalMediaReferences.Count(item => item.OwnerId == marker.Id);
    }
}
