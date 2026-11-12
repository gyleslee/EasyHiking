using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using HikingGuide.Application.Guides;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Application.Media;
using HikingGuide.Infrastructure.Media;

namespace HikingGuide.App;

public partial class ImportWindow : Window
{
    private readonly ProjectImportSession _session;
    private readonly HikingProject _project;
    private readonly ProjectImportService _importService;
    private readonly List<WaypointRow> _waypoints;
    private readonly List<TrackRow> _tracks;
    private readonly IExternalMediaDownloadService? _downloader;
    private int _previewRevision;

    public ImportWindow(ProjectImportSession session, HikingProject project, ProjectImportService importService,
        IExternalMediaDownloadService? downloader = null)
    {
        InitializeComponent();
        _session = session;
        _project = project;
        _importService = importService;
        _downloader = downloader;
        var sourceFiles = session.SourceFile.Split("; ", StringSplitOptions.RemoveEmptyEntries);
        FileLabel.Text = $"已选择 {sourceFiles.Length} 个文件：{string.Join("、", sourceFiles.Select(System.IO.Path.GetFileName))}";
        FileLabel.ToolTip = session.SourceFile;
        _tracks = session.Parsed.Tracks.Select(t => new TrackRow(t, session.SelectedTrackIds.Contains(t.Id), this)).ToList();
        TrackGrid.ItemsSource = _tracks;
        var choices = project.Tracks.Select(t => new TrackChoice(t.Id, $"[已有] {t.Name ?? "未命名轨迹"}"))
            .Concat(session.Parsed.Tracks.Select(t => new TrackChoice(t.Id, $"[新导入] {t.Name ?? "未命名轨迹"}"))).ToArray();
        GuideTrackCombo.ItemsSource = choices;
        GuideTrackCombo.SelectedValue = session.SelectedGuideTrackId;
        GuideTrackCombo.IsEnabled = project.MainGuideTrackId is null;
        NoTrackHint.Text = choices.Length == 0 ? "当前没有可用于攻略节点吸附的轨迹；仍可导入普通标注。" : "";
        _waypoints = session.WaypointSelections.Select(s => new WaypointRow(s, session.Parsed)).ToList();
        WaypointGrid.ItemsSource = _waypoints;
        OwnerTrackColumn.ItemsSource = new[] { new TrackOwnerChoice(null, "未关联") }
            .Concat(project.Tracks.Select(t => new TrackOwnerChoice(t.Id, $"[已有] {t.Name ?? "未命名轨迹"}")))
            .Concat(session.Parsed.Tracks.Select(t => new TrackOwnerChoice(t.Id, t.Name ?? "未命名轨迹"))).ToArray();
        var typeChoices = Enum.GetValues<WaypointType>().Select(t => new TypeChoice(t, GetTypeName(t))).ToArray();
        TypeColumn.ItemsSource = typeChoices;
        BatchTypeCombo.ItemsSource = typeChoices;
        BatchTypeCombo.SelectedValue = WaypointType.Normal;
        SummaryLabel.Text = _waypoints.Count == 0 ? "该文件没有标注点。" : $"共 {_waypoints.Count} 个标注点；距轨迹仅供参考，不自动勾选攻略。";
        UpdateGuideAvailability();
    }

    private static string GetTypeName(WaypointType type) => MarkerTypeNames.Get(type);

    private void WaypointSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MediaPreviewPanel is null || WaypointGrid.SelectedItem is not WaypointRow row) return;
        MediaPreviewPanel.Children.Clear();
        foreach (var attachment in _session.Parsed.MediaAttachments.Where(item => item.OwnerId == row.Id)
                     .OrderBy(item => item.DisplayOrder))
        {
            var blob = _session.Parsed.MediaBlobs.FirstOrDefault(item => item.Id == attachment.MediaBlobId);
            if (blob?.PendingBytes is not { } bytes) continue;
            try
            {
                using var input = new System.IO.MemoryStream(bytes, writable: false);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 120;
                bitmap.StreamSource = input;
                bitmap.EndInit();
                bitmap.Freeze();
                MediaPreviewPanel.Children.Add(new Image
                {
                    Source = bitmap, Width = 110, Height = 90, Stretch = System.Windows.Media.Stretch.Uniform,
                    Margin = new Thickness(0, 0, 8, 0), ToolTip = attachment.OriginalFileName ?? "项目图片"
                });
            }
            catch { MediaPreviewPanel.Children.Add(new TextBlock { Text = "图片预览失败", Margin = new Thickness(0, 0, 8, 0) }); }
        }
        foreach (var reference in _session.Parsed.ExternalMediaReferences.Where(item => item.OwnerId == row.Id)
                     .OrderBy(item => item.DisplayOrder))
        {
            var host = Uri.TryCreate(reference.Url, UriKind.Absolute, out var url) ? url.Host : "未知来源";
            var preview = new StackPanel { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
            preview.Children.Add(new TextBlock
            {
                Text = $"网络图片：{host}", TextWrapping = TextWrapping.Wrap,
                ToolTip = reference.Url
            });
            if (_downloader is not null)
            {
                var button = new Button { Content = "下载并预览", Margin = new Thickness(0, 5, 0, 0), ToolTip = "仅在点击后联网；下载成功的图片将在确认导入后加入项目。" };
                button.Click += (_, _) =>
                {
                    var previewProject = new HikingProject { Name = "导入图片预览" };
                    previewProject.Waypoints.Add(row.Selection.Waypoint);
                    previewProject.ExternalMediaReferences.Add(reference);
                    foreach (var blob in _session.Parsed.MediaBlobs) previewProject.MediaBlobs.Add(blob);
                    new MediaViewerWindow(previewProject, row.Id, _downloader,
                        async (item, downloaded) =>
                        {
                            if (_session.Parsed.MediaAttachments.Any(attachment => attachment.OwnerId == item.OwnerId &&
                                attachment.SourceKind == MediaSourceKind.ExternalUrl && attachment.SourceReference == item.Url)) return;
                            var prepared = await new ProjectMediaContentService().PrepareSaveExternalAsync(
                                previewProject, item, downloaded);
                            if (prepared.NewBlob is not null)
                            {
                                previewProject.MediaBlobs.Add(prepared.NewBlob);
                                _session.Parsed.MediaBlobs.Add(prepared.NewBlob);
                            }
                            _session.Parsed.MediaAttachments.Add(prepared.Attachment);
                            row.NotifyPictureChanged();
                        }, automaticSaveMessage: "图片已加入本次导入；确认导入后进入项目。")
                    { Owner = this }.ShowDialog();
                    WaypointSelectionChanged(this, null!);
                };
                preview.Children.Add(button);
            }
            MediaPreviewPanel.Children.Add(preview);
        }
        if (MediaPreviewPanel.Children.Count == 0)
            MediaPreviewPanel.Children.Add(new TextBlock { Text = "无图片" });
    }

    private async void GuideTrackChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_session is null) return;
        _session.SelectedGuideTrackId = GuideTrackCombo.SelectedValue as Guid?;
        foreach (var row in _tracks) row.NotifyMainChanged();
        UpdateGuideAvailability();
        if (_importService is null || _waypoints is null) return;
        var revision = ++_previewRevision;
        GuideTrackCombo.IsEnabled = false;
        SummaryLabel.Text = "正在后台计算距轨迹预览……";
        try
        {
            await Task.Run(() => _importService.Preview(_session, _project));
            if (revision != _previewRevision) return;
            foreach (var row in _waypoints) row.NotifyDistanceChanged();
            SummaryLabel.Text = $"共 {_waypoints.Count} 个标注点；距轨迹仅供参考，不自动勾选攻略。";
        }
        catch (Exception exception) { SummaryLabel.Text = $"距轨迹预览失败：{exception.Message}"; }
        finally { GuideTrackCombo.IsEnabled = true; }
    }
    private void AllGuideClicked(object sender, RoutedEventArgs e) => SetRows(r => r.IsGuideNode = true);
    private void InvertGuideClicked(object sender, RoutedEventArgs e) => SetRows(r => r.IsGuideNode = !r.IsGuideNode);
    private void AllImportClicked(object sender, RoutedEventArgs e) => SetRows(r => r.Import = true);
    private void InvertImportClicked(object sender, RoutedEventArgs e) => SetRows(r => r.Import = !r.Import);
    private void SelectedGuideClicked(object sender, RoutedEventArgs e) => SetSelected(r => r.IsGuideNode = true);
    private void SelectedNormalClicked(object sender, RoutedEventArgs e) => SetSelected(r => r.IsGuideNode = false);
    private void SelectedImportClicked(object sender, RoutedEventArgs e) => SetSelected(r => r.Import = true);
    private void SelectedExcludeClicked(object sender, RoutedEventArgs e) => SetSelected(r => r.Import = false);
    private void SelectedTypeClicked(object sender, RoutedEventArgs e)
    {
        if (BatchTypeCombo.SelectedValue is WaypointType type) SetSelected(r => r.Type = type);
    }
    private void SetRows(Action<WaypointRow> update) { foreach (var row in _waypoints) update(row); }
    private void SetSelected(Action<WaypointRow> update) { foreach (var row in WaypointGrid.SelectedItems.OfType<WaypointRow>()) update(row); }

    private void AllTracksClicked(object sender, RoutedEventArgs e)
    {
        foreach (var row in _tracks) row.Import = true;
    }

    private void InvertTracksClicked(object sender, RoutedEventArgs e)
    {
        foreach (var row in _tracks) row.Import = !row.Import;
    }

    private void ClearMainTrackClicked(object sender, RoutedEventArgs e)
    {
        if (_project.MainGuideTrackId is not null) return;
        GuideTrackCombo.SelectedIndex = -1;
        _session.SelectedGuideTrackId = null;
        UpdateGuideAvailability();
    }

    private void MainTrackChecked(object sender, RoutedEventArgs e)
    {
        if (_session is null || _project.MainGuideTrackId is not null || sender is not System.Windows.Controls.RadioButton { DataContext: TrackRow row }) return;
        row.Import = true;
        GuideTrackCombo.SelectedValue = row.Track.Id;
    }

    private void UpdateGuideAvailability()
    {
        if (_waypoints is null) return;
        var enabled = _session.SelectedGuideTrackId is not null;
        GuideColumn.IsReadOnly = !enabled;
        AllGuideButton.IsEnabled = enabled;
        InvertGuideButton.IsEnabled = enabled;
        if (!enabled)
        {
            foreach (var row in _waypoints) row.IsGuideNode = false;
            NoTrackHint.Text = "当前未选择主攻略轨迹，标注只能作为普通标注导入。";
        }
        else NoTrackHint.Text = "";
    }

    private void ConfirmClicked(object sender, RoutedEventArgs e)
    {
        WaypointGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        WaypointGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _session.SelectedTrackIds.Clear();
        foreach (var track in _tracks.Where(t => t.Import)) _session.SelectedTrackIds.Add(track.Track.Id);
        if (_tracks.Any(t => t.Track.Id == _session.SelectedGuideTrackId && !t.Import))
        {
            MessageBox.Show(this, "所选攻略轨迹未勾选导入。请先勾选该轨迹，或改选已有轨迹。", "无法导入", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_waypoints.Any(w => w.Import && w.IsGuideNode) && _session.SelectedGuideTrackId is null)
        {
            MessageBox.Show(this, "请先选择攻略轨迹，或把攻略节点改为普通标注。", "无法导入", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_waypoints.Any(w => w.Import && w.Type == WaypointType.Custom && string.IsNullOrWhiteSpace(w.CustomTypeName)))
        {
            MessageBox.Show(this, "自定义类型需要填写名称。", "无法导入", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_waypoints.Any(w => w.Import && w.RelatedTrackId is Guid owner &&
            _tracks.Any(track => track.Track.Id == owner && !track.Import)))
        {
            MessageBox.Show(this, "有标注归属于未勾选导入的轨迹。请勾选该轨迹，或在“归属轨迹”列调整标注归属。",
                "无法导入", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        foreach(var row in _tracks.Where(t=>t.Import))
        {
            if(row.HasGap&&row.RouteKind=="请选择…"){MessageBox.Show(this,"请为有间隙的轨迹选择一条行进路线或轨迹合集。","请选择路线性质",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
            row.Track.Metadata[HikingGuide.Core.Services.TrackKilometerService.RouteKindKey]=row.RouteKind=="轨迹合集"?"collection":"route";
            HikingGuide.Core.Services.TrackKilometerService.SetEnabled(row.Track,false);
        }
        DialogResult = true;
    }

    private sealed record TrackChoice(Guid Id, string Name);
    private sealed record TrackOwnerChoice(Guid? Id, string Name);
    private sealed record TypeChoice(WaypointType Code, string Name);
    private sealed class TrackRow(Track track, bool import, ImportWindow owner) : INotifyPropertyChanged
    {
        private bool _import = import;
        public Track Track { get; } = track;
        public bool Import { get => _import; set { _import = value; if (!value && IsMain && owner._project.MainGuideTrackId is null) owner.ClearMainTrackClicked(this, new RoutedEventArgs()); PropertyChanged?.Invoke(this, new(nameof(Import))); } }
        public bool IsMain => owner._session?.SelectedGuideTrackId == Track.Id;
        public void NotifyMainChanged() => PropertyChanged?.Invoke(this, new(nameof(IsMain)));
        public bool HasGap=>HikingGuide.Core.Services.TrackKilometerService.RequiresKindConfirmation(Track)||Track.Segments.Count>1&&Track.Segments.Zip(Track.Segments.Skip(1),(a,b)=>a.EndPoint is not null&&b.StartPoint is not null&&HikingGuide.Core.Services.GeoDistanceCalculator.Instance.CalculateDistance(a.EndPoint.Coordinate,b.StartPoint.Coordinate)>.001).Any(x=>x);
        public string[] RouteKinds {get;}=["请选择…","一条行进路线","轨迹合集"];
        public string RouteKind {get;set;}=HikingGuide.Core.Services.TrackKilometerService.RequiresKindConfirmation(track)?"请选择…":track.Metadata.GetValueOrDefault(HikingGuide.Core.Services.TrackKilometerService.RouteKindKey)=="collection"?"轨迹合集":"一条行进路线";
        public string Name => Track.Name ?? "（未命名轨迹）";
        public int SegmentCount => Track.Segments.Count;
        public string PointCount => Track.GetPointCount().ToString("N0");
        public string DistanceKm => (Track.GetTotalLength() / 1000).ToString("F2");
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private sealed class WaypointRow(ImportWaypointSelection selection,
        HikingGuide.Application.TrackFiles.TrackImportResult parsed) : INotifyPropertyChanged
    {
        public ImportWaypointSelection Selection => selection;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
        public bool Import { get => selection.Import; set { selection.Import = value; Changed(); Changed(nameof(IsGuideNode)); } }
        public bool IsGuideNode { get => selection.IsGuideNode; set { selection.IsGuideNode = value; Changed(); Changed(nameof(Import)); } }
        public string Name => selection.Waypoint.Name ?? "未命名标注";
        public Guid Id => selection.Waypoint.Id;
        public Guid? RelatedTrackId { get => selection.RelatedTrackId; set { selection.RelatedTrackId = value; Changed(); } }
        public bool SourceGuideIdentity => selection.Waypoint.OriginalAttributes.GetValueOrDefault("hg:isGuideNode") == "true";
        public string PictureSummary
        {
            get
            {
                var internalCount = parsed.MediaAttachments.Count(item => item.OwnerId == Id);
                var externalCount = parsed.ExternalMediaReferences.Count(item => item.OwnerId == Id);
                if (internalCount + externalCount == 0) return "—";
                return externalCount == 0 ? $"{internalCount}张" : internalCount == 0
                    ? $"{externalCount}张网络图片" : $"{internalCount}张 + {externalCount}张网络";
            }
        }
        public WaypointType Type { get => selection.Waypoint.Type; set { selection.Waypoint.Type = value; Changed(); } }
        public string? CustomTypeName { get => selection.Waypoint.CustomTypeName; set { selection.Waypoint.CustomTypeName = value; Changed(); } }
        public string SourceElevation => selection.Waypoint.SourceElevation?.ToString("F1") ?? "—";
        public string DistanceToTrack => selection.SnapPreview?.Position?.DistanceToTrack.ToString("F1") ?? "—";
        public void NotifyDistanceChanged() => Changed(nameof(DistanceToTrack));
        public void NotifyPictureChanged() => Changed(nameof(PictureSummary));
    }
}
