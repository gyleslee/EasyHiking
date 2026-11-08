using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Globalization;
using System.Diagnostics;
using HikingGuide.Application.Elevation;
using HikingGuide.Core.Elevation;
using HikingGuide.Core.Geometry;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Elevation;
using ScottPlot.WPF;

namespace HikingGuide.App;

public partial class ElevationAnalysisControl : UserControl
{
    private sealed class GpsOnlyProvider : IElevationProvider
    {
        public static readonly GpsOnlyProvider Instance = new();
        public ValueTask<double?> SampleAsync(Guid demDatabaseId, GeoCoordinate coordinate,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("DEM高程运行库不可用，请检查软件安装文件。");
    }
    private static readonly string ChartFontName = RegisterChineseChartFont();
    private sealed record SourceChoice(Guid? DatabaseId, string Name)
    {
        public override string ToString() => Name;
    }

    private HikingProject? _project;
    private ElevationTargetDescriptor? _target;
    private ElevationAnalysisResult? _leftResult;
    private ElevationAnalysisResult? _rightResult;
    private CancellationTokenSource? _cancellation;
    private bool _analysisActive;
    private bool _updatingChoices;
    private ElevationAnalysisProfile _profile = new();
    private long _selectionVersion;
    private long? _targetTrackRevision;
    private long _analysisRequestId;
    private int _selectedSplitCount;
    private SharedElevationViewportState? _sharedViewport;
    private ElevationVisibleY _leftVisibleY;
    private ElevationVisibleY _rightVisibleY;
    private WpfPlot? _panningPlot;
    private System.Windows.Point _panStart;
    private bool _panHasMoved;
    private ElevationAnalysisResult? _panResult;
    private IReadOnlyList<ElevationGuideMarker> _leftGuideMarkers = [], _rightGuideMarkers = [];
    private ElevationChartViewportState _panStartRange;
    private readonly ElevationChartViewportProfile _viewportProfile = new();
    public double LastViewportAutomationMilliseconds { get; private set; }
    private ScottPlot.Plottables.VerticalLine? _leftCursor;
    private ScottPlot.Plottables.VerticalLine? _rightCursor;
    private long _lastHoverTick;
    private readonly HashSet<Guid> _warnedPartialResults = [];
    private readonly HashSet<string> _warnedNoCoverage = [];
    private IReadOnlyList<SourceChoice> _allSources = [];
    private CancellationTokenSource? _terrainCancellation;
    private bool _loadingTerrainThreshold;

    public ElevationAnalysisControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loadingTerrainThreshold = true;
            FlatThresholdBox.Text = (Host?.GlobalSettingsService.Current.FlatSlopeThresholdPercent ?? new LocusTerrainDistanceProfile().FlatSlopeThresholdPercent).ToString(CultureInfo.CurrentCulture);
            _loadingTerrainThreshold = false;
            RefreshSources(); SetAnalysisActive(FindActiveTab());
        };
        LeftPlot.SizeChanged += (_, _) => RedrawOverlays(LeftPlot);
        RightPlot.SizeChanged += (_, _) => RedrawOverlays(RightPlot);
    }

    private bool FindActiveTab() => Window.GetWindow(this) is MainWindow window &&
        window.IsAnalysisTabActive;

    public void SetAnalysisActive(bool active)
    {
        if (_analysisActive == active) return;
        _analysisActive = active;
        if (!active) { _cancellation?.Cancel(); return; }
        QueueAutomaticAnalysis();
    }

    private App? Host => System.Windows.Application.Current as App;

    private static string RegisterChineseChartFont()
    {
        var fonts = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        foreach (var name in new[] { "simhei.ttf", "NotoSansSC-VF.ttf" })
        {
            var file = System.IO.Path.Combine(fonts, name);
            if (!System.IO.File.Exists(file)) continue;
            try
            {
                ScottPlot.Fonts.AddFontFile("易行中文图表", file, false, false);
                ScottPlot.Fonts.AddFontFile("易行中文图表", file, true, false);
                return "易行中文图表";
            }
            catch (Exception)
            {
                // 字体不可读时仍允许应用和分析功能启动。
            }
        }
        return "Microsoft YaHei";
    }

    public void SelectTarget(HikingProject? project, object? model, int selectedSplitCount = 0)
    {
        _selectedSplitCount = selectedSplitCount;
        if (selectedSplitCount > 1)
        {
            _cancellation?.Cancel();
            ++_analysisRequestId;
            _leftResult = null;
            _rightResult = null;
            _sharedViewport = null;
            LeftPlot.Plot.Clear();
            RightPlot.Plot.Clear();
            LeftGuideOverlay.Children.Clear(); RightGuideOverlay.Children.Clear();
            LeftPlot.Refresh();
            RightPlot.Refresh();
            LeftFillOverlay.Children.Clear();
            RightFillOverlay.Children.Clear();
            LeftHoverOverlay.Children.Clear();
            RightHoverOverlay.Children.Clear();
            LeftSummary.Text = "暂无分析结果";
            RightSummary.Text = "暂无分析结果";
            AnalysisStatus.Text = "当前选择了多个拆分分段，请选择一个分段进行高程分析。";
            return;
        }
        var next = (project, model) switch
        {
            ({ } p, Track track) => ElevationAnalysisCoordinator.ForTrack(p, track),
            ({ } p, SplitSection section) => ElevationAnalysisCoordinator.ForSplitSection(p, section),
            ({ } p, GuideSegment segment) => TryGuideSegment(p, segment),
            _ => null
        };
        var revision = next is null ? null : project?.Tracks.FirstOrDefault(item =>
            item.Id == next.TrackId)?.GeometryRevision;
        if (ReferenceEquals(_project, project) && _target?.TargetId == next?.TargetId &&
            _target?.TargetType == next?.TargetType && _targetTrackRevision == revision)
        {
            _target = next;
            UpdateGuideMarkers(LeftPlot, _leftResult); UpdateGuideMarkers(RightPlot, _rightResult);
            if (_analysisActive && _leftResult is null && _cancellation is null)
                QueueAutomaticAnalysis();
            return;
        }
        _cancellation?.Cancel();
        ++_selectionVersion;
        ++_analysisRequestId;
        _project = project;
        _target = next;
        _targetTrackRevision = revision;
        _sharedViewport = null;
        _leftVisibleY = default;
        _rightVisibleY = default;
        TargetLabel.Text = _target is null ? "轨迹：请选择轨迹或拆分分段" :
            $"轨迹：{(_target.TargetType == ElevationTargetType.SplitSection ?
                project?.Tracks.FirstOrDefault(item => item.Id == _target.TrackId)?.Name + " / " : "")}{_target.DisplayName}";
        _leftResult = null;
        _rightResult = null;
        _leftCursor = null;
        _rightCursor = null;
        LeftPlot.Plot.Clear();
        RightPlot.Plot.Clear();
        LeftGuideOverlay.Children.Clear(); RightGuideOverlay.Children.Clear();
        LeftFillOverlay.Children.Clear();
        RightFillOverlay.Children.Clear();
        LeftHoverOverlay.Children.Clear();
        RightHoverOverlay.Children.Clear();
        LeftPlot.Refresh();
        RightPlot.Refresh();
        LeftSummary.Text = "暂无分析结果";
        RightSummary.Text = "暂无分析结果";
        AnalysisStatus.Text = _target is null ? "请选择可分析的轨迹或拆分分段。" :
            "正在准备自动分析…";
        if (_analysisActive) QueueAutomaticAnalysis();
    }

    private static ElevationTargetDescriptor? TryGuideSegment(HikingProject project,
        GuideSegment segment)
    {
        try { return ElevationAnalysisCoordinator.ForGuideSegment(project, segment); }
        catch (InvalidOperationException) { return null; }
    }

    public void RefreshSources()
    {
        if (Host is not { } app) return;
        var oldLeft = (LeftSource.SelectedItem as SourceChoice)?.DatabaseId;
        var oldRight = (RightSource.SelectedItem as SourceChoice)?.DatabaseId;
        _updatingChoices = true;
        var choices = new List<SourceChoice> { new(null, "GPS原始海拔") };
        choices.AddRange(app.DemCatalog.Databases.Select(database =>
            new SourceChoice(database.Id, $"{database.Name}（{(System.IO.Directory.Exists(database.RootPath) ? "已连接" : "目录未连接")}）")));
        _allSources = choices;
        LeftSource.ItemsSource = choices;
        RightSource.ItemsSource = choices.Where(choice => choice.DatabaseId.HasValue).ToArray();
        LeftSource.SelectedItem = choices.FirstOrDefault(item => item.DatabaseId == oldLeft) ?? choices[0];
        RightSource.SelectedItem = ((SourceChoice[])RightSource.ItemsSource)
            .FirstOrDefault(item => item.DatabaseId == oldRight) ??
            ((SourceChoice[])RightSource.ItemsSource).FirstOrDefault();
        ApplyMode();
        _updatingChoices = false;
        QueueAutomaticAnalysis();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyMode();
        QueueAutomaticAnalysis();
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingChoices) QueueAutomaticAnalysis();
    }

    private void QueueAutomaticAnalysis()
    {
        if (!_analysisActive || _target is null || _selectedSplitCount > 1 || _updatingChoices) return;
        var request = ++_analysisRequestId;
        _cancellation?.Cancel();
        _leftResult = null;
        _rightResult = null;
        LeftPlot.Plot.Clear();
        RightPlot.Plot.Clear();
        LeftPlot.Refresh();
        RightPlot.Refresh();
        LeftFillOverlay.Children.Clear();
        RightFillOverlay.Children.Clear();
        LeftHoverOverlay.Children.Clear();
        RightHoverOverlay.Children.Clear();
        LeftSummary.Text = "正在分析…";
        RightSummary.Text = "正在分析…";
        LeftGuideOverlay.Children.Clear(); RightGuideOverlay.Children.Clear();
        _ = Dispatcher.BeginInvoke(async () =>
        {
            if (request == _analysisRequestId && _analysisActive)
                await AnalyzeCurrentAsync(false);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ApplyMode()
    {
        if (RightPanel is null || LeftPlot is null) return;
        var mode = ModeBox.SelectedIndex;
        _updatingChoices = true;
        var selectedId = (LeftSource.SelectedItem as SourceChoice)?.DatabaseId;
        var allowed = mode == 2 ? _allSources.Where(choice => choice.DatabaseId.HasValue).ToArray() :
            _allSources.ToArray();
        LeftSource.ItemsSource = allowed;
        LeftSource.SelectedItem = allowed.FirstOrDefault(choice => choice.DatabaseId == selectedId) ??
            allowed.FirstOrDefault();
        RightPanel.Visibility = mode == 0 ? Visibility.Collapsed : Visibility.Visible;
        var grid = (Grid)RightPanel.Parent;
        grid.ColumnDefinitions[1].Width = mode == 0 ? new GridLength(0) :
            new GridLength(1, GridUnitType.Star);
        LeftSource.IsEnabled = mode != 1;
        if (mode == 1 && LeftSource.Items.Count > 0) LeftSource.SelectedIndex = 0;
        RightSource.IsEnabled = mode != 0;
        _updatingChoices = false;
    }

    private async void OnAnalyze(object sender, RoutedEventArgs e) => await AnalyzeCurrentAsync(true);

    public async Task<bool> RunAutomationGpsAsync(HikingProject project, Track track)
    {
        SelectTarget(project, track);
        RefreshSources();
        ModeBox.SelectedIndex = 0;
        LeftSource.SelectedIndex = 0;
        await AnalyzeCurrentAsync();
        return _leftResult is { IsStale: false, Series.Count: > 0 } &&
            LeftPlot.Plot.GetPlottables().Any();
    }

    public async Task<bool> RunAutomationTerrainAsync(HikingProject project, Track track)
    {
        if (!await RunAutomationGpsAsync(project, track)) return false;
        var original = _leftResult!;
        if (original.TerrainClassification is not { } classification || classification.Profile != new LocusTerrainDistanceProfile()) return false;
        var viewport = LeftPlot.Plot.Axes.GetLimits();
        FlatThresholdBox.Text = "3";
        for (var i = 0; i < 100 && _leftResult?.TerrainClassification?.Profile.FlatSlopeThresholdPercent != 3; i++)
            await Task.Delay(50);
        return _leftResult?.TerrainClassification is { } updated && updated.Profile.FlatSlopeThresholdPercent == 3 &&
            ReferenceEquals(classification.RawSeries, updated.RawSeries) && ReferenceEquals(original.Series, _leftResult.Series) &&
            original.Summary == _leftResult.Summary && viewport.Equals(LeftPlot.Plot.Axes.GetLimits()) &&
            LeftSummary.Text.Contains("平路里程") && Host!.GlobalSettingsService.Current.FlatSlopeThresholdPercent == 3;
    }

    public async Task<bool> RunAutomationNoGpsAsync(HikingProject project, Track track)
    {
        SelectTarget(project, track);
        RefreshSources();
        ModeBox.SelectedIndex = 0;
        LeftSource.SelectedIndex = 0;
        await AnalyzeCurrentAsync();
        return _leftResult?.Summary.Availability == ElevationAvailability.Unavailable;
    }

    public async Task<bool> RunAutomationSelectionAsync(HikingProject project,
        IReadOnlyList<Track> tracks)
    {
        if (tracks.Count < 3) return false;
        SetAnalysisActive(true);
        ModeBox.SelectedIndex = 0;
        LeftSource.SelectedIndex = 0;
        Guid? firstResultId = null;
        foreach (var track in tracks.Take(2))
        {
            SelectTarget(project, track);
            if (!await WaitForTargetAsync(track.Id, ElevationTargetType.Track) ||
                !RenderedTrackColor(track)) return false;
            if (firstResultId is null) firstResultId = _leftResult!.Id;
        }
        SelectTarget(project, tracks[0]);
        if (!await WaitForTargetAsync(tracks[0].Id, ElevationTargetType.Track) ||
            _leftResult?.Id != firstResultId) return false;
        SelectTarget(project, tracks[0]);
        SelectTarget(project, tracks[1]);
        SelectTarget(project, tracks[2]);
        if (!await WaitForTargetAsync(tracks[2].Id, ElevationTargetType.Track) ||
            !RenderedTrackColor(tracks[2])) return false;
        var section = tracks[2].GetSplitSections().FirstOrDefault();
        if (section is null) return false;
        SelectTarget(project, section);
        if (!await WaitForTargetAsync(section.Id, ElevationTargetType.SplitSection) ||
            !RenderedTrackColor(tracks[2])) return false;
        SetAnalysisActive(false);
        SelectTarget(project, tracks[0]);
        await Task.Delay(100);
        var didNotAnalyzeInactive = _leftResult is null;
        SetAnalysisActive(true);
        return didNotAnalyzeInactive &&
            await WaitForTargetAsync(tracks[0].Id, ElevationTargetType.Track);
    }

    private bool RenderedTrackColor(Track track) =>
        LeftPlot.Plot.GetPlottables().OfType<ScottPlot.Plottables.Scatter>()
            .FirstOrDefault()?.LineColor == ScottPlot.Color.FromHex(track.DisplayColor ?? "#2877C7");

    private async Task<bool> WaitForTargetAsync(Guid id, ElevationTargetType type)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (_leftResult is { IsStale: false } result && result.TargetId == id &&
                result.TargetType == type)
                return true;
            await Task.Delay(20);
        }
        return false;
    }

    public (bool LeftZoom, bool RightZoom, bool LeftPan, bool RightPan,
        bool FullRange, bool Hover) RunAutomationViewport()
    {
        var timer = Stopwatch.StartNew();
        if (_leftResult is null || _rightResult is null || _sharedViewport is null)
            return default;
        var total = _leftResult.Summary.DistanceMeters;
        if (total <= 0) return default;
        bool Verify()
        {
            var left = LeftPlot.Plot.Axes.GetLimits();
            var right = RightPlot.Plot.Axes.GetLimits();
            var range = _sharedViewport!.Range;
            return Math.Abs(left.Left * 1000 - range.XMinMeters) < 0.001 &&
                Math.Abs(left.Right * 1000 - range.XMaxMeters) < 0.001 &&
                Math.Abs(right.Left - left.Left) < 0.000001 &&
                Math.Abs(right.Right - left.Right) < 0.000001 &&
                left.Bottom <= _leftVisibleY.MinimumMeters &&
                left.Top >= _leftVisibleY.MaximumMeters &&
                right.Bottom <= _rightVisibleY.MinimumMeters &&
                right.Top >= _rightVisibleY.MaximumMeters;
        }
        _sharedViewport = _sharedViewport with { Range = ElevationChartViewport.Zoom(
            ElevationChartViewport.Full(total), total / 2, 120, total, _viewportProfile) };
        ApplySharedViewport();
        var leftZoom = Verify();
        _sharedViewport = _sharedViewport with { Range = ElevationChartViewport.Zoom(
            _sharedViewport.Range, total / 2, 120, total, _viewportProfile) };
        ApplySharedViewport();
        var rightZoom = Verify();
        _sharedViewport = _sharedViewport with { Range = ElevationChartViewport.Pan(
            _sharedViewport.Range, -total, total) };
        ApplySharedViewport();
        var leftPan = Verify() && _sharedViewport.Range.XMinMeters == 0;
        _sharedViewport = _sharedViewport with { Range = ElevationChartViewport.Pan(
            _sharedViewport.Range, total, total) };
        ApplySharedViewport();
        var rightPan = Verify() && _sharedViewport.Range.XMaxMeters == total;
        OnFitAll(this, new RoutedEventArgs());
        var full = Verify() && _sharedViewport.Range.XMinMeters == 0 &&
            _sharedViewport.Range.XMaxMeters == total;
        if (_leftCursor is not null) { _leftCursor.X = total / 2000; _leftCursor.IsVisible = true; LeftPlot.Refresh(); }
        if (_rightCursor is not null) { _rightCursor.X = total / 2000; _rightCursor.IsVisible = true; RightPlot.Refresh(); }
        DrawHoverBubble(LeftPlot, _leftResult, LeftHoverOverlay, total / 2);
        DrawHoverBubble(RightPlot, _rightResult, RightHoverOverlay, total / 2);
        var hover = LeftHoverOverlay.Children.Count > 0 && RightHoverOverlay.Children.Count > 0;
        LastViewportAutomationMilliseconds = timer.Elapsed.TotalMilliseconds;
        return (leftZoom, rightZoom, leftPan, rightPan, full, hover);
    }

    public async Task<(bool GpsVsDem, bool DemVsDem)> RunAutomationComparisonsAsync(
        HikingProject project, Track track)
    {
        SelectTarget(project, track);
        RefreshSources();
        if (RightSource.Items.Count < 2 || LeftSource.Items.Count < 3)
            return (false, false);
        ModeBox.SelectedIndex = 1;
        RightSource.SelectedIndex = 0;
        await AnalyzeCurrentAsync();
        var gpsVsDem = _leftResult?.Source.SourceType == ElevationSourceType.Gps &&
            _rightResult?.Source.SourceType == ElevationSourceType.Dem &&
            _rightResult.Summary.CoverageRatio > 0;
        ModeBox.SelectedIndex = 2;
        LeftSource.SelectedIndex = 0;
        RightSource.SelectedIndex = 1;
        await AnalyzeCurrentAsync();
        var demVsDem = _leftResult?.Source.DemDatabaseId is Guid leftId &&
            _rightResult?.Source.DemDatabaseId is Guid rightId && leftId != rightId &&
            _leftResult.Summary.CoverageRatio > 0 &&
            _rightResult.Summary.CoverageRatio > 0;
        if (demVsDem)
        {
            _sharedViewport = _sharedViewport is null ? null : _sharedViewport with
            { Range = ElevationChartViewport.Clamp(20, 80, _leftResult!.Summary.DistanceMeters) };
            ApplySharedViewport();
            var rightLimits = RightPlot.Plot.Axes.GetLimits();
            demVsDem = Math.Abs(rightLimits.Left - 0.02) < 1e-6 &&
                Math.Abs(rightLimits.Right - 0.08) < 1e-6;
            OnFitAll(this, new RoutedEventArgs());
        }
        return (gpsVsDem, demVsDem);
    }

    private async Task AnalyzeCurrentAsync(bool forceRefresh = false)
    {
        if (_project is not { } project || _target is not { } target ||
            Host is not { } app) return;
        var mode = ModeBox.SelectedIndex;
        var left = LeftSource.SelectedItem as SourceChoice;
        var right = RightSource.SelectedItem as SourceChoice;
        if (left is null || mode != 0 && right is null ||
            mode == 2 && left.DatabaseId is null)
        {
            AnalysisStatus.Text = "请先选择可用的高程来源。";
            return;
        }
        if (mode == 1) left = new SourceChoice(null, "GPS原始海拔");
        if ((left.DatabaseId.HasValue || mode != 0) && app.DemProvider is null)
        {
            AnalysisStatus.Text = app.DemStartupError ?? "DEM高程运行库不可用。";
            return;
        }
        if (!double.TryParse(SpacingBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture,
                out var spacing) ||
            !double.TryParse(SmoothingBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture,
                out var smoothing) ||
            !double.TryParse(SlopeBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture,
                out var slope))
        {
            AnalysisStatus.Text = "采样间隔、平滑曲线和坡度计算必须填写有效数字。";
            return;
        }
        try { _profile = new ElevationAnalysisProfile(spacing, smoothing, slope); _profile.Validate(); }
        catch (ArgumentOutOfRangeException error) { AnalysisStatus.Text = error.Message; return; }
        var profile = _profile;
        _cancellation?.Cancel();
        var requestId = ++_analysisRequestId;
        var selectionVersion = _selectionVersion;
        var selectedProjectId = project.Id;
        var targetType = target.TargetType;
        var targetId = target.TargetId;
        var selectedMode = mode;
        var leftId = left.DatabaseId;
        var rightId = right?.DatabaseId;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        AnalyzeButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        AnalysisProgress.Visibility = Visibility.Visible;
        AnalysisProgress.Value = 0;
        var coordinator = new ElevationAnalysisCoordinator(app.DemCatalog,
            (IElevationProvider?)app.DemProvider ?? GpsOnlyProvider.Instance);
        var progress = new Progress<(int Done, int Total)>(item =>
        {
            if (requestId != _analysisRequestId) return;
            if (item.Total > 0) AnalysisProgress.Value = 100d * item.Done / item.Total;
            AnalysisStatus.Text = $"正在分析：{item.Done}/{item.Total} 个里程样本";
        });
        string? geometryFingerprint = null;
        string? gpsFingerprint = null;
        Guid? activeDemId = leftId;
        try
        {
            AnalysisStatus.Text = "正在准备统一里程轴和覆盖检查…";
            geometryFingerprint = await Task.Run(() =>
                new StandardElevationAnalysisV1().GeometryFingerprint(target), cancellation.Token);
            gpsFingerprint = leftId is null || rightId is null && mode != 0 ?
                await Task.Run(() => new StandardElevationAnalysisV1().GpsFingerprint(target),
                    cancellation.Token) : null;
            var first = await AnalyzeOne(left, coordinator, project, target, progress,
                cancellation.Token, forceRefresh, profile);
            if (!Current(first, leftId)) return;
            _leftResult = first;
            Draw(LeftPlot, first, left.Name);
            LeftSummary.Text = Summarize(left.Name, first);
            WarnPartial(first);
            if (mode != 0)
            {
                activeDemId = rightId;
                var second = await AnalyzeOne(right!, coordinator, project, target, progress,
                    cancellation.Token, forceRefresh, profile);
                if (!Current(second, rightId)) return;
                _rightResult = second;
                Draw(RightPlot, second, right!.Name);
                RightSummary.Text = Summarize(right.Name, second);
                WarnPartial(second);
                var comparison = StandardElevationAnalysisV1.Compare(first, second);
                AnalysisStatus.Text = comparison.CommonValidSamples == 0 ?
                    "两来源没有共同有效样本，无法计算差值。" :
                    $"共同有效 {comparison.CommonValidSamples} 点；右减左平均差 {comparison.MeanDifferenceMeters:F1} m，平均绝对差 {comparison.MeanAbsoluteDifferenceMeters:F1} m。差值不代表精度高低。";
            }
            else
            {
                _rightResult = null;
                AnalysisStatus.Text = first.IsStale ? "分析期间轨迹或DEM已变化，结果已标记过期。" :
                    first.Summary.Availability == ElevationAvailability.Unavailable ?
                    "该来源没有可用高程；不会自动改用其他来源补洞。" :
                    "分析完成。单击曲线可定位地图，滚轮可缩放，拖动可平移；黄色圆点为攻略节点。";
            }
        }
        catch (OperationCanceledException)
        {
            if (requestId == _analysisRequestId) AnalysisStatus.Text = "分析已取消。";
        }
        catch (Exception error)
        {
            if (requestId != _analysisRequestId || !ReferenceEquals(_project, project)) return;
            if (mode == 0 && error.Message.Contains("未连接", StringComparison.Ordinal) &&
                project.ElevationAnalyses.Where(item => item.TargetType == targetType &&
                    item.TargetId == targetId && item.GeometryFingerprint == geometryFingerprint &&
                    item.Source.DemDatabaseId == leftId)
                    .OrderByDescending(item => item.CreatedAt).FirstOrDefault() is { } saved)
            {
                _leftResult = saved;
                Draw(LeftPlot, saved, saved.Source.DisplayName);
                LeftSummary.Text = (saved.Profile == profile ? "" : "历史参数结果｜") +
                    Summarize(saved.Source.DisplayName, saved);
                AnalysisStatus.Text = saved.Profile == profile ?
                    "DEM目录未连接，已显示项目保存的历史结果；重新分析需连接数据库。" :
                    "DEM目录未连接，显示的是旧分析参数的历史结果；不能当作当前参数结果。";
                return;
            }
            AnalysisStatus.Text = $"分析失败：{error.Message}";
            if (error.Message.Contains("不覆盖此轨迹所在区域", StringComparison.Ordinal))
            {
                AnalysisStatus.Text = "当前DEM不覆盖所选轨迹";
                var key = $"{project.Id}|{target.TargetId}|{geometryFingerprint}|{activeDemId}";
                if (!forceRefresh && !_warnedNoCoverage.Add(key)) return;
            }
            MessageBox.Show(Window.GetWindow(this), error.Message, "高程分析错误", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            if (requestId == _analysisRequestId)
            {
                AnalyzeButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                AnalysisProgress.Visibility = Visibility.Collapsed;
            }
        }

        bool Current(ElevationAnalysisResult result, Guid? expectedSourceId) =>
            !cancellation.IsCancellationRequested && requestId == _analysisRequestId &&
            selectionVersion == _selectionVersion && ReferenceEquals(_project, project) &&
            project.Id == selectedProjectId && _target?.TargetType == targetType &&
            _target.TargetId == targetId && ModeBox.SelectedIndex == selectedMode &&
            result.ProjectId == selectedProjectId && result.TargetType == targetType &&
            result.TargetId == targetId && !result.IsStale &&
            TryTerrainProfile(out var currentTerrainProfile) && result.TerrainClassification is { IsStale: false } terrain &&
            terrain.Profile == currentTerrainProfile &&
            result.Profile == profile && result.AnalysisProfileVersion == profile.Version &&
            result.Source.DemDatabaseId == expectedSourceId &&
            (result.Source.SourceType == ElevationSourceType.Dem
                ? Host?.DemCatalog.Get(expectedSourceId!.Value).Fingerprint == result.Source.SourceFingerprint
                : gpsFingerprint == result.Source.SourceFingerprint) &&
            geometryFingerprint == result.GeometryFingerprint;
    }

    private void WarnPartial(ElevationAnalysisResult result)
    {
        if (result.Source.SourceType != ElevationSourceType.Dem ||
            result.Summary.Availability != ElevationAvailability.Partial ||
            !_warnedPartialResults.Add(result.Id)) return;
        MessageBox.Show(Window.GetWindow(this),
            $"“{result.Source.DisplayName}”仅覆盖 {result.Summary.CoverageRatio:P1} 的分析样本。缺失区间不会插值，完整爬升、下降等统计暂不提供。",
            "DEM 部分覆盖", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task<ElevationAnalysisResult> AnalyzeOne(SourceChoice source,
        ElevationAnalysisCoordinator coordinator, HikingProject project,
        ElevationTargetDescriptor target, IProgress<(int Done, int Total)> progress,
        CancellationToken token, bool forceRefresh, ElevationAnalysisProfile profile)
    {
        var result = source.DatabaseId is Guid id ?
            await coordinator.AnalyzeDemAsync(project, target, id, profile, progress, token, forceRefresh) :
            await coordinator.AnalyzeGpsAsync(project, target, profile, progress, token, forceRefresh);
        if (!TryTerrainProfile(out var terrainProfile)) throw new ArgumentException("平路标准必须填写0至100之间的有效百分比。");
        return await coordinator.EnsureTerrainClassificationAsync(project, target, result, terrainProfile, token);
    }

    private bool TryTerrainProfile(out LocusTerrainDistanceProfile profile)
    {
        var valid = double.TryParse(FlatThresholdBox?.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var threshold) &&
            double.IsFinite(threshold) && threshold >= 0 && threshold <= 100;
        profile = new LocusTerrainDistanceProfile(valid ? threshold : new LocusTerrainDistanceProfile().FlatSlopeThresholdPercent);
        return valid;
    }

    private async void OnFlatThresholdChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingTerrainThreshold || !IsLoaded || Host is not { } app) return;
        _terrainCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _terrainCancellation = cancellation;
        try
        {
            await Task.Delay(250, cancellation.Token);
            if (!TryTerrainProfile(out var profile)) { AnalysisStatus.Text = "平路标准必须填写0至100之间的有效百分比。"; return; }
            var interruptedAnalysis = _cancellation is not null;
            _cancellation?.Cancel();
            app.GlobalSettingsService.Current.FlatSlopeThresholdPercent = profile.FlatSlopeThresholdPercent;
            await app.GlobalSettingsService.SaveAsync(cancellation.Token);
            var project = _project; var target = _target;
            var selectionVersion = _selectionVersion;
            var left = _leftResult; var right = _rightResult;
            if (project is null || target is null) return;
            AnalysisStatus.Text = "正在复用高程缓存更新里程分类…";
            var coordinator = new ElevationAnalysisCoordinator(app.DemCatalog, (IElevationProvider?)app.DemProvider ?? GpsOnlyProvider.Instance);
            if (left is not null) left = await coordinator.EnsureTerrainClassificationAsync(project, target, left, profile, cancellation.Token);
            if (right is not null) right = await coordinator.EnsureTerrainClassificationAsync(project, target, right, profile, cancellation.Token);
            if (cancellation.IsCancellationRequested || selectionVersion != _selectionVersion || !ReferenceEquals(_project, project)) return;
            // 不重画曲线，不改变共享缩放和悬停状态。
            if (ReferenceEquals(_leftResult, left) || _leftResult?.Id == left?.Id)
            { _leftResult = left; if (left is not null) LeftSummary.Text = Summarize(left.Source.DisplayName, left); }
            if (ReferenceEquals(_rightResult, right) || _rightResult?.Id == right?.Id)
            { _rightResult = right; if (right is not null) RightSummary.Text = Summarize(right.Source.DisplayName, right); }
            AnalysisStatus.Text = "里程分类已更新，海拔曲线保持不变。";
            if (interruptedAnalysis) QueueAutomaticAnalysis();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!cancellation.IsCancellationRequested) AnalysisStatus.Text = $"里程分类更新失败：{error.Message}"; }
        finally { if (ReferenceEquals(_terrainCancellation, cancellation)) _terrainCancellation = null; }
    }

    private void Draw(WpfPlot plot, ElevationAnalysisResult result, string title)
    {
        plot.Plot.Clear();
        plot.Plot.Font.Set(ChartFontName);
        var style = _project is null ? new ElevationChartStyle("#2877C7", new()) :
            ElevationChartStyleResolver.Resolve(_project, result.TrackId);
        var color = ScottPlot.Color.FromHex(style.ColorHex);
        foreach (var run in ElevationChartRuns.Build(result.Series))
        {
            var line = plot.Plot.Add.Scatter(run.Select(point => point.DistanceMeters / 1000).ToArray(),
                run.Select(point => point.ProcessedElevationMeters!.Value).ToArray());
            line.LineColor = color;
            line.LineWidth = style.Profile.LineWidth;
            line.MarkerSize = 0;
        }
        plot.Plot.Title(title);
        plot.Plot.XLabel("距起点 (km)");
        UpdateGuideMarkers(plot, result);
        var cursor = plot.Plot.Add.VerticalLine(0);
        cursor.IsVisible = false;
        cursor.EnableAutoscale = false;
        cursor.Color = color.WithAlpha(0.65);
        cursor.LineWidth = 1.5f;
        if (ReferenceEquals(plot, LeftPlot)) _leftCursor = cursor;
        else _rightCursor = cursor;
        if (_sharedViewport is not { } state || state.TargetId != result.TargetId ||
            state.GeometryFingerprint != result.GeometryFingerprint)
            _sharedViewport = new(result.TargetId, result.GeometryFingerprint,
                ElevationChartViewport.Full(result.Summary.DistanceMeters));
        ApplySharedViewport();
    }

    private static string Summarize(string name, ElevationAnalysisResult result)
    {
        var s = result.Summary;
        static string Value(double? value) => value.HasValue ? $"{value:F1}" : "—";
        var source = result.Source.SourceType == ElevationSourceType.Gps ? "GPS海拔完整度" : "DEM完整度";
        return $"{name}｜{source}{s.CoverageRatio:P1}｜" +
            $"距离 {s.DistanceMeters / 1000:F2} km｜最低海拔/最高海拔 {Value(s.MinimumElevationMeters)}/{Value(s.MaximumElevationMeters)} m｜" +
            $"爬升/下降 {Value(s.AscentMeters)}/{Value(s.DescentMeters)} m" +
            (result.TerrainClassification is { } terrain ?
                $"｜爬升里程 {terrain.UphillDistanceMeters / 1000:F2} km｜下降里程 {terrain.DownhillDistanceMeters / 1000:F2} km｜平路里程 {terrain.FlatDistanceMeters / 1000:F2} km" +
                $"｜平路标准 |坡度| ≤ {terrain.Profile.FlatSlopeThresholdPercent:0.###}%" +
                (terrain.UnclassifiedDistanceMeters > 0 ? $"｜未分类 {terrain.UnclassifiedDistanceMeters / 1000:F2} km" : "") +
                (terrain.IsStale || result.IsStale ? "｜里程分类已过期" : "") : "｜里程分类尚未计算");
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private void OnFitAll(object sender, RoutedEventArgs e)
    {
        if (_sharedViewport is null || _leftResult is null) return;
        _sharedViewport = _sharedViewport with
        { Range = ElevationChartViewport.Full(_leftResult.Summary.DistanceMeters) };
        ApplySharedViewport();
    }

    public void RefreshStyle()
    {
        if (_leftResult is not null) Draw(LeftPlot, _leftResult, _leftResult.Source.DisplayName);
        if (_rightResult is not null && RightPanel.Visibility == Visibility.Visible)
            Draw(RightPlot, _rightResult, _rightResult.Source.DisplayName);
    }

    private void ApplySharedViewport()
    {
        if (_sharedViewport is not { } shared) return;
        Apply(LeftPlot, _leftResult, ref _leftVisibleY);
        if (RightPanel.Visibility == Visibility.Visible)
            Apply(RightPlot, _rightResult, ref _rightVisibleY);

        void Apply(WpfPlot plot, ElevationAnalysisResult? result, ref ElevationVisibleY previous)
        {
            if (result is null) return;
            var range = shared.Range;
            plot.Plot.Axes.SetLimitsX(range.XMinMeters / 1000,
                Math.Max(range.XMaxMeters / 1000, range.XMinMeters / 1000 + 0.000001));
            var visible = ElevationChartViewport.FitVisibleY(result.Series, range, previous,
                _viewportProfile);
            previous = visible;
            plot.Plot.Axes.SetLimitsY(visible.MinimumMeters, visible.MaximumMeters);
            plot.Refresh();
            _ = Dispatcher.BeginInvoke(() => RedrawOverlays(plot),
                System.Windows.Threading.DispatcherPriority.Render);
            if (!visible.HasData) AnalysisStatus.Text = "当前可见范围无高程数据";
        }
    }

    private void OnOpenDatabases(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) window.OpenDemDatabaseManager();
    }

    private void OnLeftHover(object sender, MouseEventArgs e) => ShowHover(LeftPlot, _leftResult, e);
    private void OnRightHover(object sender, MouseEventArgs e) => ShowHover(RightPlot, _rightResult, e);

    private void ShowHover(WpfPlot plot, ElevationAnalysisResult? result, MouseEventArgs e)
    {
        if (_panningPlot is not null || result is null || result.Series.Count == 0) return;
        var now = Environment.TickCount64;
        if (now - _lastHoverTick < 17) return;
        _lastHoverTick = now;
        var coordinate = plot.Plot.GetCoordinates(plot.GetPlotPixelPosition(e));
        var index = ElevationChartViewport.NearestIndex(result.Series, coordinate.X * 1000);
        if (index < 0) return;
        var nearest = result.Series[index];
        var chainage = nearest.DistanceMeters;
        _sharedViewport = _sharedViewport is null ? null :
            _sharedViewport with { HoverChainageMeters = chainage };
        AnalysisStatus.Text = $"距起点 {chainage / 1000:F2} km｜" +
            (RightPanel.Visibility == Visibility.Visible ? "" :
                $"坡度 {FormatSlope(nearest.SlopePercent)}｜") +
            $"纬度 {nearest.Coordinate.Latitude:F6}｜经度 {nearest.Coordinate.Longitude:F6}";
        var cursorKm = chainage / 1000;
        if (_leftCursor is not null)
        {
            _leftCursor.X = cursorKm;
            _leftCursor.IsVisible = true;
            LeftPlot.Refresh();
            DrawHoverBubble(LeftPlot, _leftResult, LeftHoverOverlay, chainage);
        }
        if (_rightCursor is not null && RightPanel.Visibility == Visibility.Visible)
        {
            _rightCursor.X = cursorKm;
            _rightCursor.IsVisible = true;
            RightPlot.Refresh();
            DrawHoverBubble(RightPlot, _rightResult, RightHoverOverlay, chainage);
        }
        if (Window.GetWindow(this)?.DataContext is MainViewModel main)
            _ = main.Map.SetElevationHoverAsync(nearest.Coordinate);
    }

    private static string FormatSlope(double? slope) => slope is double value &&
        double.IsFinite(value) ? $"{value:+0.0;-0.0;0.0}%" : "—";

    private void DrawHoverBubble(WpfPlot plot, ElevationAnalysisResult? result,
        Canvas overlay, double chainage)
    {
        overlay.Children.Clear();
        if (result is null || result.Series.Count == 0 || _project is null) return;
        var index = ElevationChartViewport.NearestIndex(result.Series, chainage);
        if (index < 0) return;
        var point = result.Series[index];
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
            ElevationChartStyleResolver.Resolve(_project, result.TrackId).ColorHex);
        var height = point.ProcessedElevationMeters;
        var visibleY = ReferenceEquals(plot, LeftPlot) ? _leftVisibleY : _rightVisibleY;
        var y = height ?? (visibleY.MinimumMeters + visibleY.MaximumMeters) / 2;
        var pixel = PlotPoint(plot, chainage / 1000, y);
        var plotTop = PlotPoint(plot, chainage / 1000, visibleY.MaximumMeters).Y;
        var bubble = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(248, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(165, color.R, color.G, color.B)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 3, 6, 3),
            Child = new TextBlock
            {
                Text = height is double value ? $"{value:F0} m\n坡度 {FormatSlope(point.SlopePercent)}" :
                    "无海拔数据",
                Foreground = Brushes.Black, FontSize = 12, TextAlignment = TextAlignment.Center
            }
        };
        bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = bubble.DesiredSize.Width;
        var heightDip = bubble.DesiredSize.Height;
        Canvas.SetLeft(bubble, Math.Clamp(pixel.X - width / 2, 0,
            Math.Max(0, overlay.ActualWidth - width)));
        Canvas.SetTop(bubble, pixel.Y - heightDip - 12 < plotTop ? pixel.Y + 10 :
            pixel.Y - heightDip - 12);
        overlay.Children.Add(bubble);
        if (height.HasValue)
        {
            var marker = new Ellipse
            {
                Width = 10, Height = 10, Fill = new SolidColorBrush(color),
                Stroke = Brushes.White, StrokeThickness = 2
            };
            Canvas.SetLeft(marker, pixel.X - 5);
            Canvas.SetTop(marker, pixel.Y - 5);
            overlay.Children.Add(marker);
        }
    }

    private void OnPlotLeave(object sender, MouseEventArgs e)
    {
        if (_panningPlot is not null) return;
        if (_leftCursor is not null) { _leftCursor.IsVisible = false; LeftPlot.Refresh(); }
        if (_rightCursor is not null) { _rightCursor.IsVisible = false; RightPlot.Refresh(); }
        LeftHoverOverlay.Children.Clear();
        RightHoverOverlay.Children.Clear();
        _sharedViewport = _sharedViewport is null ? null :
            _sharedViewport with { HoverChainageMeters = null };
        if (Window.GetWindow(this)?.DataContext is MainViewModel main)
            _ = main.Map.SetElevationHoverAsync(null);
    }

    private void OnPlotWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not WpfPlot plot || _sharedViewport is not { } shared ||
            _leftResult is null) return;
        e.Handled = true;
        var mouse = plot.Plot.GetCoordinates(plot.GetPlotPixelPosition(e));
        _sharedViewport = shared with { Range = ElevationChartViewport.Zoom(shared.Range,
            mouse.X * 1000, e.Delta, _leftResult.Summary.DistanceMeters, _viewportProfile) };
        ApplySharedViewport();
    }

    private void OnPlotPanStart(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WpfPlot plot || _sharedViewport is null) return;
        e.Handled = true;
        _panningPlot = plot;
        _panStart = e.GetPosition(plot);
        _panHasMoved = false;
        _panResult = ReferenceEquals(plot, LeftPlot) ? _leftResult : _rightResult;
        _panStartRange = _sharedViewport.Range;
        plot.CaptureMouse();
    }

    private void OnPlotPanMove(object sender, MouseEventArgs e)
    {
        if (sender is not WpfPlot plot || !ReferenceEquals(plot, _panningPlot) ||
            _sharedViewport is not { } shared || _leftResult is null) return;
        e.Handled = true;
        var movement = e.GetPosition(plot) - _panStart;
        if (!_panHasMoved && Math.Abs(movement.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(movement.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _panHasMoved = true;
        var x0 = PlotPoint(plot, _panStartRange.XMinMeters / 1000, 0).X;
        var x1 = PlotPoint(plot, _panStartRange.XMaxMeters / 1000, 0).X;
        var width = x1 - x0;
        if (Math.Abs(width) < 1) return;
        var delta = -(e.GetPosition(plot).X - _panStart.X) / width *
            (_panStartRange.XMaxMeters - _panStartRange.XMinMeters);
        _sharedViewport = shared with { Range = ElevationChartViewport.Pan(_panStartRange,
            delta, _leftResult.Summary.DistanceMeters) };
        ApplySharedViewport();
    }

    private async void OnPlotPanEnd(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WpfPlot plot || !ReferenceEquals(plot, _panningPlot)) return;
        e.Handled = true;
        _panningPlot = null;
        plot.ReleaseMouseCapture();
        var result = ReferenceEquals(plot, LeftPlot) ? _leftResult : _rightResult;
        if (_panHasMoved || result is null || !ReferenceEquals(result, _panResult) || result.Series.Count == 0) return;
        var position = e.GetPosition(plot);
        if ((position - _panStart).Length >= Math.Max(SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance)) return;
        var coordinate = plot.Plot.GetCoordinates(plot.GetPlotPixelPosition(e)); var limits = plot.Plot.Axes.GetLimits();
        if (coordinate.X < limits.Left || coordinate.X > limits.Right || coordinate.Y < limits.Bottom || coordinate.Y > limits.Top) return;
        var index = ElevationChartViewport.NearestIndex(result.Series, coordinate.X * 1000); if (index < 0) return;
        var point = result.Series[index].Coordinate;
        if (_project is { } project && _target is { } target)
        {
            var marker = (ReferenceEquals(plot, LeftPlot) ? _leftGuideMarkers : _rightGuideMarkers).FirstOrDefault(m => (PlotPoint(plot, m.DistanceMeters / 1000, m.ElevationMeters) - position).Length <= 7);
            if (marker is not null) point = marker.Coordinate;
        }
        if (Window.GetWindow(this)?.DataContext is MainViewModel main)
            try { await main.Map.CenterElevationPointAsync(point); }
            catch (Exception error) { AnalysisStatus.Text = $"地图定位失败：{error.Message}"; }
    }

    private void OnPlotLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _panningPlot)) _panningPlot = null;
    }

    private System.Windows.Point PlotPoint(WpfPlot plot, double x, double y)
    {
        var pixel = plot.Plot.GetPixel(new ScottPlot.Coordinates(x, y));
        var dpi = VisualTreeHelper.GetDpi(plot);
        return new(pixel.X / dpi.DpiScaleX, pixel.Y / dpi.DpiScaleY);
    }

    internal (System.Windows.Point Screen, GeoCoordinate Coordinate) LinkProbe(bool node)
    {
        var marker = _leftGuideMarkers[1];
        var sample = _leftResult!.Series[_leftResult.Series.Count / 3];
        return node ? (LeftPlot.PointToScreen(PlotPoint(LeftPlot, marker.DistanceMeters / 1000, marker.ElevationMeters)), marker.Coordinate)
            : (LeftPlot.PointToScreen(PlotPoint(LeftPlot, sample.DistanceMeters / 1000, sample.ProcessedElevationMeters!.Value)), sample.Coordinate);
    }

    internal bool GuideDotsProbe(int count) => LeftGuideOverlay.Children.Count == count && LeftGuideOverlay.Children.OfType<Ellipse>().All(e => e.Width == 8 && e.Fill == Brushes.Yellow && e.ToolTip is null);

    private void UpdateGuideMarkers(WpfPlot plot, ElevationAnalysisResult? result)
    {
        var markers = result is not null && _project is { } project && _target is { } target
            ? ElevationGuideMarkers.Build(project, target, result) : [];
        if (ReferenceEquals(plot, LeftPlot)) _leftGuideMarkers = markers; else _rightGuideMarkers = markers;
        DrawGuideMarkers(plot, result);
    }

    private void DrawGuideMarkers(WpfPlot plot, ElevationAnalysisResult? result)
    {
        var canvas = ReferenceEquals(plot, LeftPlot) ? LeftGuideOverlay : RightGuideOverlay;
        canvas.Children.Clear();
        if (result is null || _sharedViewport is null || canvas.ActualWidth <= 0) return;
        var limits = plot.Plot.Axes.GetLimits();
        foreach (var marker in ReferenceEquals(plot, LeftPlot) ? _leftGuideMarkers : _rightGuideMarkers)
        {
            var x = marker.DistanceMeters / 1000;
            if (x < limits.Left || x > limits.Right || marker.ElevationMeters < limits.Bottom || marker.ElevationMeters > limits.Top) continue;
            var pixel = PlotPoint(plot, x, marker.ElevationMeters);
            var dot = new Ellipse { Width = 8, Height = 8, Fill = Brushes.Yellow, Stroke = Brushes.DarkGoldenrod, StrokeThickness = 1, Tag = marker.NodeId };
            Canvas.SetLeft(dot, pixel.X - 4); Canvas.SetTop(dot, pixel.Y - 4); canvas.Children.Add(dot);
        }
    }

    private void RedrawOverlays(WpfPlot plot)
    {
        DrawGuideMarkers(plot, ReferenceEquals(plot, LeftPlot) ? _leftResult : _rightResult);
        var result = ReferenceEquals(plot, LeftPlot) ? _leftResult : _rightResult;
        var canvas = ReferenceEquals(plot, LeftPlot) ? LeftFillOverlay : RightFillOverlay;
        canvas.Children.Clear();
        if (result is null || _sharedViewport is null || _project is null ||
            canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;
        var visible = ReferenceEquals(plot, LeftPlot) ? _leftVisibleY : _rightVisibleY;
        var range = _sharedViewport.Range;
        var top = PlotPoint(plot, range.XMinMeters / 1000, visible.MaximumMeters);
        var bottom = PlotPoint(plot, range.XMaxMeters / 1000, visible.MinimumMeters);
        canvas.Clip = new RectangleGeometry(new Rect(top.X, top.Y,
            Math.Max(0, bottom.X - top.X), Math.Max(0, bottom.Y - top.Y)));
        var style = ElevationChartStyleResolver.Resolve(_project, result.TrackId);
        var baseColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
            style.ColorHex);
        var brush = new LinearGradientBrush(
            System.Windows.Media.Color.FromArgb(style.Profile.FillTopAlpha,
                baseColor.R, baseColor.G, baseColor.B),
            System.Windows.Media.Color.FromArgb(style.Profile.FillBottomAlpha,
                baseColor.R, baseColor.G, baseColor.B), 90);
        var series = result.Series;
        var start = Math.Max(0, ElevationChartViewport.NearestIndex(series, range.XMinMeters) - 1);
        var end = Math.Min(series.Count, ElevationChartViewport.NearestIndex(series,
            range.XMaxMeters) + 2);
        foreach (var run in ElevationChartRuns.Build(series, start, end))
        {
            if (run.Count < 2) continue;
            var first = PlotPoint(plot, run[0].DistanceMeters / 1000,
                run[0].ProcessedElevationMeters!.Value);
            var last = PlotPoint(plot, run[^1].DistanceMeters / 1000,
                run[^1].ProcessedElevationMeters!.Value);
            var figure = new PathFigure { StartPoint = new System.Windows.Point(first.X, bottom.Y),
                IsClosed = true };
            foreach (var point in run)
                figure.Segments.Add(new LineSegment(PlotPoint(plot,
                    point.DistanceMeters / 1000, point.ProcessedElevationMeters!.Value), true));
            figure.Segments.Add(new LineSegment(new System.Windows.Point(last.X, bottom.Y), true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Fill = brush });
        }
    }
}
