using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.App;

/// <summary>Track 和逻辑拆分分段共用的顺序、方向和地图定位面板。</summary>
public partial class TrackMergeWindow : Window
{
    private readonly bool _connect;
    private readonly bool _splitMode;
    private readonly Action<object>? _focusGeometry;
    private readonly Action<GeoCoordinate>? _focusPoint;
    private readonly ObservableCollection<GeometrySelectionRow> _rows = [];

    public TrackMergeWindow(HikingProject project, bool connect, IReadOnlySet<Guid>? preselected = null,
        Action<object>? focusGeometry = null, Action<GeoCoordinate>? focusPoint = null,
        IReadOnlyList<TrackMergeSource>? geometrySelection = null, bool restrictToSelection = true)
        : this(connect, false, focusGeometry, focusPoint)
    {
        Title = connect ? "连接轨迹" : "合并轨迹";
        IntroText.Text = "可混选普通轨迹与拆分分段，调整顺序和方向。主攻略轨迹及其分段不可参与，请先转为普通轨迹（节点同步转换）。同一整轨与其分段不可重复选择。";
        CreateButton.Content = connect ? "创建连接轨迹" : "创建合并轨迹";
        NameBox.Text = connect ? "新连接轨迹" : "新合并轨迹";
        var selectedKeys=geometrySelection?.Select(s=>(s.TrackId,s.SectionId)).ToHashSet()
            ??preselected?.Select(id=>(id,(Guid?)null)).ToHashSet()??[];
        var selectedOnly=restrictToSelection&&selectedKeys.Count>=2;
        IntroText.Text+=(selectedOnly?" 当前仅列出已多选的轨迹或分段。":"")+
            (connect?" 请手动填写全部勾选项的连接顺序；编号须从1开始连续且不重复。":"");
        foreach (var track in project.Tracks.Where(t => t.Id != project.MainGuideTrackId))
        {
            if(!selectedOnly||selectedKeys.Contains((track.Id,null)))
            AddRow(new GeometrySelectionRow(track, track.Id, track.Name ?? "（未命名轨迹）",
                track.StartPoint, track.EndPoint,
                geometrySelection is not null ? geometrySelection.Any(item => item.TrackId == track.Id && item.SectionId is null) :
                preselected is null || preselected.Count == 0 || preselected.Contains(track.Id)));
            foreach (var section in track.GetSplitSections())
                if(!selectedOnly||selectedKeys.Contains((track.Id,section.Id)))
                    AddRow(new GeometrySelectionRow(section, section.Id,
                        $"    {track.Name ?? "未命名轨迹"} / {section.Name}", section.StartPoint, section.EndPoint,
                        geometrySelection?.Any(item => item.TrackId == track.Id && item.SectionId == section.Id) == true));
        }
        TrackList.ItemsSource = _rows;
        RefreshPreview();
    }

    public TrackMergeWindow(Track track, bool connect, IReadOnlySet<Guid>? preselected,
        Action<object>? focusGeometry = null, Action<GeoCoordinate>? focusPoint = null)
        : this(connect, true, focusGeometry, focusPoint)
    {
        Title = connect ? "连接拆分分段" : "合并拆分分段";
        CreateButton.Content = connect ? "连接" : "合并";
        NamePanel.Visibility = Visibility.Collapsed;
        IntroText.Text = $"当前轨迹：{track.Name ?? "（未命名轨迹）"}。选择至少两个拆分分段，调整顺序和方向；点击列表可在地图定位。";
        foreach (var section in track.GetSplitSections())
            if(preselected is null||preselected.Count<2||preselected.Contains(section.Id))
            AddRow(new GeometrySelectionRow(section, section.Id, section.Name,
                section.StartPoint, section.EndPoint,
                preselected is not null && preselected.Contains(section.Id)));
        TrackList.ItemsSource = _rows;
        RefreshPreview();
    }

    private TrackMergeWindow(bool connect, bool splitMode, Action<object>? focusGeometry,
        Action<GeoCoordinate>? focusPoint)
    {
        InitializeComponent();
        _connect = connect;
        RouteKindPanel.Visibility=connect||splitMode?Visibility.Collapsed:Visibility.Visible;
        _splitMode = splitMode;
        _focusGeometry = focusGeometry;
        _focusPoint = focusPoint;
        IncludeHeader.Text=connect?"是否连接":"是否合并";
        OrderHeader.Visibility=connect?Visibility.Visible:Visibility.Collapsed;
        OrderHeaderColumn.Width=new GridLength(connect?110:0);
    }

    private void AddRow(GeometrySelectionRow row)
    {
        row.ShowOrder=_connect;
        row.PropertyChanged += (_, e) =>
        {
            if(e.PropertyName==nameof(GeometrySelectionRow.IsIncluded))
            {
                if(row.IsIncluded)ExcludeOverlaps(row);
            }
            RefreshPreview();
        };
        _rows.Add(row);
        if (row.IsIncluded) ExcludeOverlaps(row);
    }

    private void ExcludeOverlaps(GeometrySelectionRow selected)
    {
        foreach (var row in _rows.Where(row => row != selected && row.IsIncluded))
            if (selected.Source is Track track && row.Source is SplitSection section && section.TrackId == track.Id ||
                selected.Source is SplitSection split && row.Source is Track parent && split.TrackId == parent.Id)
                row.IsIncluded = false;
    }

    public string NewTrackName => NameBox.Text.Trim();
    public bool IsSingleJourney => _connect||RouteKindCombo.SelectedIndex==0;
    public bool DeleteOriginalTracks => DeleteOriginalCheck.IsChecked == true;
    private GeometrySelectionRow[] OrderedRows()
    {
        var rows=_rows.Where(r=>r.IsIncluded).ToArray();
        return _connect?TrackConnectionOrder.OrderedIndices(rows.Select(r=>r.OrderText).ToArray()).Select(i=>rows[i]).ToArray():rows;
    }
    public IReadOnlyList<TrackMergeSource> SelectedSources => OrderedRows()
        .Select(row => row.Source is SplitSection section
            ? new TrackMergeSource(section.TrackId, row.IsReversed, section.Id)
            : new TrackMergeSource(row.Id, row.IsReversed)).ToArray();
    public IReadOnlyList<SplitSectionSource> SelectedSplitSources => OrderedRows().Where(row=>row.Source is SplitSection)
        .Select(row => new SplitSectionSource(row.Id, row.IsReversed)).ToArray();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = TrackList.SelectedItem as GeometrySelectionRow;
        StartButton.IsEnabled = EndButton.IsEnabled = row is not null;
        if (row is not null) _focusGeometry?.Invoke(row.Source);
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int offset)
    {
        if (TrackList.SelectedItem is not GeometrySelectionRow row) return;
        if(_connect&&row.IsIncluded)
        {
            GeometrySelectionRow[] ordered;
            try{ordered=OrderedRows();}catch(ArgumentException){ordered=[];}
            if(ordered.Length>0)
            {
            var index=Array.IndexOf(ordered,row);var nextOrder=index+offset;
            if(nextOrder<0||nextOrder>=ordered.Length)return;
            (row.OrderText,ordered[nextOrder].OrderText)=(ordered[nextOrder].OrderText,row.OrderText);
            var targetIndex=_rows.IndexOf(ordered[nextOrder]);var sourceIndex=_rows.IndexOf(row);
            _rows.Move(sourceIndex,targetIndex);TrackList.SelectedItem=row;RefreshPreview();return;
            }
        }
        var oldIndex = _rows.IndexOf(row);
        var next = oldIndex + offset;
        if (next < 0 || next >= _rows.Count) return;
        _rows.Move(oldIndex, next);
        TrackList.SelectedItem = row;
        RefreshPreview();
    }

    private void OnToStart(object sender, RoutedEventArgs e) => FocusEndpoint(true);
    private void OnToEnd(object sender, RoutedEventArgs e) => FocusEndpoint(false);

    private void FocusEndpoint(bool start)
    {
        if (TrackList.SelectedItem is not GeometrySelectionRow row) return;
        var point = (start != row.IsReversed ? row.StartPoint : row.EndPoint)?.Coordinate;
        if (point is { } coordinate) _focusPoint?.Invoke(coordinate);
    }

    private void RefreshPreview()
    {
        if (PreviewText is null) return;
        if(_connect&&_rows.Count(r=>r.IsIncluded)>=2&&_rows.Where(r=>r.IsIncluded).All(r=>string.IsNullOrWhiteSpace(r.OrderText)))
        {PreviewText.Text="请先手动填写所有勾选项的连接顺序（必填），从1开始连续填写且不能重复。";return;}
        GeometrySelectionRow[] selected;
        try{selected=OrderedRows();}catch(ArgumentException){PreviewText.Text=TrackConnectionOrder.ErrorMessage;return;}
        if (selected.Length < 2)
        {
            PreviewText.Text = "请至少选择两个轨迹或拆分分段。";
            return;
        }
        if (!_connect)
        {
            PreviewText.Text = "合并保留各连续几何 Part 的空间间隙，不补画直线，也不计入间隙距离。";
            return;
        }
        var distance = GeoDistanceCalculator.Instance;
        var lines = new List<string>
        {
            "下列连接均为直线几何，不代表道路或可通行路线；不调用 BRouter。"
        };
        for (var index = 1; index < selected.Length; index++)
        {
            var previous = selected[index - 1];
            var next = selected[index];
            var from = previous.IsReversed ? previous.StartPoint : previous.EndPoint;
            var to = next.IsReversed ? next.EndPoint : next.StartPoint;
            if (from is null || to is null) continue;
            lines.Add($"{previous.DisplayName} → {next.DisplayName}：" +
                $"{distance.CalculateDistance(from.Coordinate, to.Coordinate):F1} m");
        }
        PreviewText.Text = string.Join("\n", lines);
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        if(_connect){try{_=OrderedRows();}catch(ArgumentException){ShowOrderWarning();return;}}
        if ((_splitMode ? SelectedSplitSources.Count : SelectedSources.Count) < 2)
        {
            MessageBox.Show(this, "请至少选择两个轨迹或拆分分段。",
                "无法执行操作", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_splitMode && string.IsNullOrWhiteSpace(NewTrackName))
        {
            MessageBox.Show(this, "请输入新轨迹名称。", "无法创建轨迹",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
    private void ShowOrderWarning()=>MessageBox.Show(this,TrackConnectionOrder.ErrorMessage,"连接顺序有误",MessageBoxButton.OK,MessageBoxImage.Warning);

    private sealed class GeometrySelectionRow(object source, Guid id, string displayName,
        TrackPoint? startPoint, TrackPoint? endPoint, bool included) : INotifyPropertyChanged
    {
        private bool _isIncluded = included;
        private bool _isReversed;
        private string _orderText="";
        public bool ShowOrder {get;set;}
        public string OrderText {get=>_orderText;set{if(_orderText==value)return;_orderText=value;OnPropertyChanged();}}
        public event PropertyChangedEventHandler? PropertyChanged;
        public object Source { get; } = source;
        public Guid Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public TrackPoint? StartPoint { get; } = startPoint;
        public TrackPoint? EndPoint { get; } = endPoint;
        public bool IsIncluded
        {
            get => _isIncluded;
            set { if (_isIncluded == value) return; _isIncluded = value; OnPropertyChanged(); }
        }
        public bool IsReversed
        {
            get => _isReversed;
            set { if (_isReversed == value) return; _isReversed = value; OnPropertyChanged(); }
        }
        private void OnPropertyChanged([CallerMemberName] string? property = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
