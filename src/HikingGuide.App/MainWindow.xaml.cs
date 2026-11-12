using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
using System.IO;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;

namespace HikingGuide.App;

public sealed record P83TreeSmokeResult(
    bool ShortNameStartsLeft, bool ShortNameNeedsNoScrollBar,
    bool CompleteSingleLineNames, bool LongNameHasScrollBar,
    bool LongNameStartsLeft, bool ProjectSelectionPreservesOffset,
    bool TrackSelectionPreservesOffset, bool MarkerSelectionPreservesOffset,
    bool ManualScrollWorks, bool HorizontalScrollLeavesProjectClean,
    bool ManualPositionSurvivesSelection,
    bool ManualPositionSurvivesExpandCollapse, bool DirtyIndicatorShown,
    bool ManualPositionSurvivesDirtyRefresh,
    bool ManualPositionSurvivesRename, bool MapSelectionPreservesOffset,
    bool ActiveProjectSwitchPreservesOffset,
    double ExtentWidth, double ViewportWidth, double ManualOffset)
{
    public bool Success => ShortNameStartsLeft && ShortNameNeedsNoScrollBar &&
        CompleteSingleLineNames && LongNameHasScrollBar &&
        LongNameStartsLeft && ProjectSelectionPreservesOffset && TrackSelectionPreservesOffset &&
        MarkerSelectionPreservesOffset && ManualScrollWorks &&
        HorizontalScrollLeavesProjectClean && ManualPositionSurvivesSelection &&
        ManualPositionSurvivesExpandCollapse && DirtyIndicatorShown &&
        ManualPositionSurvivesDirtyRefresh &&
        ManualPositionSurvivesRename && MapSelectionPreservesOffset &&
        ActiveProjectSwitchPreservesOffset;
}

public partial class MainWindow : Window
{
    private bool _closeApproved;
    private bool _closeInProgress;
    private BRouterSettingsWindow? _bRouterSettingsWindow;
    private DemDatabaseWindow? _demDatabaseWindow;
    private ScrollViewer? _projectTreeScroll;
    private ScrollBar? _projectTreeHorizontalBar;
    private bool _treeHorizontalInitialized;
    private double _requestedTreeHorizontalOffset;
    private bool _treeHorizontalDrag;
    private bool _treeHorizontalRestoreQueued;
    private bool _treeHorizontalRestoring;
    public bool IsAnalysisTabActive => BottomTabs.SelectedIndex == 2 && BottomTabs.Visibility==Visibility.Visible && AnalysisTab.Visibility==Visibility.Visible;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => RestorePanelLayout();
        viewModel.GuideEditsCommit = () => GuideWorkbench.CommitEdits();
        viewModel.AnalysisSelectionChanged += (_, _) => GuideWorkbench.SelectProject(viewModel.MapContextProject);
        viewModel.CloseRequested += (_, _) => Close();
        viewModel.RoutePlanningStarted += (_, _) => ShowBottomPanel(1,true);
        viewModel.OpenBRouterDataRequested += (_, _) => OnOpenBRouterSettings(this,
            new RoutedEventArgs());
        viewModel.AnalysisSelectionChanged += (_, _) => UpdateAnalysisSelection();
        viewModel.TrackColorChanged += (_, _) => ElevationAnalysis?.RefreshStyle();
        ProjectTree.Loaded += OnProjectTreeLoaded;
        ProjectTree.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler(OnProjectTreeRequestBringIntoView), true);
        ProjectTree.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnProjectTreePreviewMouseDown), true);
        ProjectTree.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnProjectTreePreviewMouseUp), true);
        Closing += OnClosing;
        ((App)System.Windows.Application.Current).FavoritesChanged += async (_, _) =>
        {
            FavoritesVisibleMenu.IsChecked = ((App)System.Windows.Application.Current).GlobalSettingsService.Current.ShowFavoritesOnMap;
            await FavoriteAction(FavoritesContent.RefreshAsync);
        };
        viewModel.Map.FavoritesEvent += async (_, e) =>
        {
            if (e.Type == "FavoriteClicked" && e.FavoriteId is Guid id)
                try { var details = await ((App)System.Windows.Application.Current).Favorites.GetAsync(id); if (details is not null) await viewModel.Map.LocateFavoriteAsync(details.Point); }
                catch (Exception error) { MessageBox.Show(this, error.Message, "收藏点定位失败"); }
        };
        Loaded += async (_, _) => { FavoritesVisibleMenu.IsChecked = ((App)System.Windows.Application.Current).GlobalSettingsService.Current.ShowFavoritesOnMap; await FavoriteAction(FavoritesContent.RefreshAsync); };
    }

    private async Task FavoriteAction(Func<Task> action) { try { await action(); } catch (Exception error) { MessageBox.Show(this, error.Message, "收藏点操作失败", MessageBoxButton.OK, MessageBoxImage.Warning); } }
    private void OnOpenFirstRunSetup(object sender,RoutedEventArgs e)
    {if(DataContext is MainViewModel main)new FirstRunSetupWindow(this,main.Map).ShowDialog();}
    private void OnOpenFavorites(object sender, RoutedEventArgs e) => ShowBottomPanel(3);
    public void ShowFavoritesForAutomation() => BottomTabs.SelectedIndex = 3;
    private async void OnNewFavorite(object sender, RoutedEventArgs e) => await FavoriteAction(() => FavoritesContent.AddAsync(new(23.1, 113.3)));
    private async void OnCreateFavoriteCollection(object sender, RoutedEventArgs e) => await FavoriteAction(FavoritesContent.CreateCollectionAsync);
    private async void OnRenameFavoriteCollection(object sender, RoutedEventArgs e) => await FavoriteAction(FavoritesContent.RenameCollectionAsync);
    private async void OnDeleteFavoriteCollection(object sender, RoutedEventArgs e) => await FavoriteAction(FavoritesContent.DeleteCollectionAsync);
    private void OnFavoriteManager(object sender, RoutedEventArgs e) => FavoritesContent.OpenManager();
    private async void OnImportFavorites(object sender, RoutedEventArgs e) => await FavoriteAction(FavoritesContent.ImportAsync);
    private async void OnExportFavorites(object sender, RoutedEventArgs e) => await FavoriteAction(FavoritesContent.ExportAsync);
    private async void OnExportFavoritesKmz(object sender, RoutedEventArgs e) => await FavoriteAction(() => FavoritesContent.ExportKmzAsync());
    private async void OnFavoriteVisibility(object sender, RoutedEventArgs e) => await FavoriteAction(async () =>
    { var app = (App)System.Windows.Application.Current; app.GlobalSettingsService.Current.ShowFavoritesOnMap = FavoritesVisibleMenu.IsChecked; await app.GlobalSettingsService.SaveAsync(); app.NotifyFavoritesChanged(); });
    private async void OnCopyFavorite(object sender, RoutedEventArgs e)
    {
        var node = sender is MenuItem menuItem && ItemsControl.ItemsControlFromItemContainer(menuItem) is ContextMenu { PlacementTarget: FrameworkElement target } ? target.DataContext as NavigationNode : null;
        if (node?.Model is MapMarkerBase marker && DataContext is MainViewModel main && main.MapContextProject is { } project)
            await FavoriteAction(() => FavoritesContent.CopyMarkerAsync(project, marker));
    }

    private void OnProjectTreeLoaded(object sender, RoutedEventArgs e)
    {
        if (_projectTreeScroll is not null)
            _projectTreeScroll.ScrollChanged -= OnProjectTreeScrollChanged;
        if (_projectTreeHorizontalBar is not null)
            _projectTreeHorizontalBar.Scroll -= OnProjectTreeHorizontalBarScroll;
        _projectTreeScroll = FindDescendant<ScrollViewer>(ProjectTree);
        if (_projectTreeScroll is null) return;
        _projectTreeScroll.ScrollChanged += OnProjectTreeScrollChanged;
        _projectTreeHorizontalBar = FindDescendant<ScrollBar>(_projectTreeScroll,
            bar => bar.Orientation == Orientation.Horizontal);
        if (_projectTreeHorizontalBar is not null)
            _projectTreeHorizontalBar.Scroll += OnProjectTreeHorizontalBarScroll;
        if (!_treeHorizontalInitialized)
        {
            _treeHorizontalInitialized = true;
            _requestedTreeHorizontalOffset = 0;
        }
        RestoreProjectTreeHorizontalOffset();
    }

    private void OnProjectTreeHorizontalBarScroll(object sender, ScrollEventArgs e) =>
        _requestedTreeHorizontalOffset = e.NewValue;

    private void OnProjectTreePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is { Orientation: Orientation.Horizontal })
            _treeHorizontalDrag = true;
    }

    private void OnProjectTreePreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_treeHorizontalDrag) return;
        Dispatcher.BeginInvoke(() => _treeHorizontalDrag = false, DispatcherPriority.Background);
    }

    private void OnProjectTreeRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject is TreeViewItem ||
            FindAncestor<TreeViewItem>(e.TargetObject as DependencyObject) is not null)
            RestoreProjectTreeHorizontalOffset();
    }

    private void OnProjectTreeScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_projectTreeScroll is null || _treeHorizontalRestoring) return;
        if (Math.Abs(e.HorizontalChange) > 0.01)
        {
            if (_treeHorizontalDrag)
                _requestedTreeHorizontalOffset = _projectTreeScroll.HorizontalOffset;
            else RestoreProjectTreeHorizontalOffset();
        }
        if (Math.Abs(e.ExtentWidthChange) > 0.01 || Math.Abs(e.ViewportWidthChange) > 0.01)
            RestoreProjectTreeHorizontalOffset();
    }

    private void RestoreProjectTreeHorizontalOffset()
    {
        if (_projectTreeScroll is null || _treeHorizontalRestoreQueued) return;
        _treeHorizontalRestoreQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _treeHorizontalRestoreQueued = false;
            if (_projectTreeScroll is null || _treeHorizontalDrag) return;
            var target = Math.Clamp(_requestedTreeHorizontalOffset, 0, _projectTreeScroll.ScrollableWidth);
            if (Math.Abs(_projectTreeScroll.HorizontalOffset - target) < 0.01) return;
            _treeHorizontalRestoring = true;
            try { _projectTreeScroll.ScrollToHorizontalOffset(target); }
            finally { _treeHorizontalRestoring = false; }
        }, DispatcherPriority.ContextIdle);
    }

    internal void SetProjectTreeHorizontalOffsetForAutomation(double offset)
    {
        if (_projectTreeScroll is null || _projectTreeHorizontalBar is null)
            throw new InvalidOperationException("项目树水平滚动条尚未加载。");
        var target = Math.Clamp(offset, 0, _projectTreeScroll.ScrollableWidth);
        _projectTreeHorizontalBar.RaiseEvent(new ScrollEventArgs(ScrollEventType.ThumbTrack, target)
        { RoutedEvent = ScrollBar.ScrollEvent });
        _projectTreeScroll.ScrollToHorizontalOffset(target);
    }

    public async Task<P83TreeSmokeResult> RunP83TreeSmokeAsync(string longProjectPath)
    {
        if (DataContext is not MainViewModel viewModel || _projectTreeScroll is null)
            throw new InvalidOperationException("项目树尚未准备好。");
        async Task FlushAsync()
        {
            ProjectTree.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            ProjectTree.UpdateLayout();
        }
        bool At(double value) => Math.Abs(_projectTreeScroll.HorizontalOffset - value) < 1.5;
        await FlushAsync();
        var shortLeft = At(0);
        var shortNeedsNoScroll = _projectTreeScroll.ScrollableWidth < 1;
        await viewModel.OpenForAutomationAsync(longProjectPath);
        await FlushAsync();
        var project = viewModel.MapContextProject ?? throw new InvalidOperationException("长名称项目未加载。");
        var track = project.Tracks.Single();
        var marker = project.Waypoints.Single();
        NavigationNode Root() => viewModel.NavigationNodes.Single(node => ReferenceEquals(node.Model, project));
        NavigationNode TrackNode() => Root().Children.Single(node => ReferenceEquals(node.Model, track));
        NavigationNode MarkerNode() => TrackNode().Children.SelectMany(node => node.Children)
            .Single(node => ReferenceEquals(node.Model, marker));
        Root().IsExpanded = true;
        TrackNode().IsExpanded = true;
        var markerGroup = TrackNode().Children.Single(node => node.Children.Any(child => ReferenceEquals(child.Model, marker)));
        markerGroup.IsExpanded = true;
        await FlushAsync();
        var labels = new[] { project, (object)track, marker }.Select(model =>
            FindDescendant<TextBlock>(ProjectTree, block => ReferenceEquals(block.DataContext is NavigationNode node
                ? node.Model : null, model))).ToArray();
        var completeNames = labels.All(label => label is not null &&
            label.TextTrimming == TextTrimming.None && label.TextWrapping == TextWrapping.NoWrap &&
            double.IsPositiveInfinity(label.MaxWidth) && label.Text.Length >= 200);
        var longScroll = _projectTreeScroll.ScrollableWidth > 100 &&
            _projectTreeHorizontalBar?.Visibility == Visibility.Visible;
        var longLeft = At(0);
        Root().IsSelected = true;
        await FlushAsync();
        var projectLeft = At(0);
        TrackNode().IsSelected = true;
        await FlushAsync();
        var trackLeft = At(0);
        MarkerNode().IsSelected = true;
        await FlushAsync();
        var markerLeft = At(0);
        SetProjectTreeHorizontalOffsetForAutomation(500);
        await FlushAsync();
        var manual = _projectTreeScroll.HorizontalOffset;
        var manualWorks = manual > 100;
        var scrollLeavesProjectClean = !project.IsDirty;
        Root().IsSelected = true;
        await FlushAsync();
        TrackNode().IsSelected = true;
        await FlushAsync();
        MarkerNode().IsSelected = true;
        await FlushAsync();
        var selectionPreserved = At(manual);
        TrackNode().IsExpanded = false;
        await FlushAsync();
        TrackNode().IsExpanded = true;
        TrackNode().Children.Single(node => node.Children.Any(child => ReferenceEquals(child.Model, marker))).IsExpanded = true;
        await FlushAsync();
        var expandPreserved = At(manual);
        project.Description = "P8-3 Dirty 测试";
        viewModel.RefreshProjectTreeForAutomation();
        await FlushAsync();
        var dirtyIndicatorShown = viewModel.WindowTitle.Contains(" *", StringComparison.Ordinal);
        var dirtyPreserved = At(manual);
        track.Name = new string('改', 240);
        viewModel.RefreshProjectTreeForAutomation();
        await FlushAsync();
        var renamePreserved = At(manual);
        viewModel.RunMapMarkerSelectionForAutomation(marker.Id);
        await FlushAsync();
        var mapPreserved = At(manual);
        var shortRoot = viewModel.NavigationNodes.First(node => node.Model is HikingProject other &&
            !ReferenceEquals(other, project));
        viewModel.SelectNavigationNode(shortRoot, false);
        await FlushAsync();
        viewModel.SelectNavigationNode(Root(), false);
        await FlushAsync();
        var projectSwitchPreserved = At(manual);
        SetProjectTreeHorizontalOffsetForAutomation(0);
        await FlushAsync();
        return new P83TreeSmokeResult(shortLeft, shortNeedsNoScroll,
            completeNames, longScroll, longLeft,
            projectLeft, trackLeft, markerLeft, manualWorks, scrollLeavesProjectClean,
            selectionPreserved,
            expandPreserved, dirtyIndicatorShown, dirtyPreserved,
            renamePreserved, mapPreserved,
            projectSwitchPreserved, _projectTreeScroll.ExtentWidth,
            _projectTreeScroll.ViewportWidth, manual);
    }

    private static T? FindDescendant<T>(DependencyObject source, Func<T, bool>? matches = null)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(source); i++)
        {
            var child = VisualTreeHelper.GetChild(source, i);
            if (child is T result && (matches is null || matches(result))) return result;
            if (FindDescendant(child, matches) is T nested) return nested;
        }
        return null;
    }

    public void ApproveAutomationClose() => _closeApproved = true;

    private void OnOpenBRouterSettings(object sender, RoutedEventArgs e)
    {
        if (_bRouterSettingsWindow is { IsVisible: true })
        {
            _bRouterSettingsWindow.Activate();
            return;
        }
        if (System.Windows.Application.Current is not App { BRouterController: { } controller }) return;
        _bRouterSettingsWindow = new BRouterSettingsWindow(controller)
        { Owner = this };
        _bRouterSettingsWindow.Show();
    }

    private long _navigationSelectionVersion;
    private void OnNavigationSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var version=++_navigationSelectionVersion;
        if(DataContext is not MainViewModel viewModel||viewModel.IsProjectTreeRefreshActive||e.NewValue is not NavigationNode node)return;
        var focus=!viewModel.IsMapSelectionInProgress;
        // IsSelected can be applied while WPF is evaluating a recycled container's Style.
        // Never clear/rebuild ItemsSource inside that property-change stack.
        Dispatcher.BeginInvoke(()=>
        {
            if(version!=_navigationSelectionVersion||!IsLoaded||!ReferenceEquals(DataContext,viewModel)||viewModel.IsProjectTreeRefreshActive||!ReferenceEquals(ProjectTree.SelectedItem,node))return;
            viewModel.SelectNavigationNode(node,focus);
        },DispatcherPriority.ContextIdle);
    }

    private void OnBottomTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, BottomTabs) || ElevationAnalysis is null) return;
        GuideWorkbench?.SelectProject((DataContext as MainViewModel)?.MapContextProject);
        UpdateAnalysisSelection();
        ElevationAnalysis.SetAnalysisActive(IsAnalysisTabActive);
    }

    private void UpdateAnalysisSelection()
    {
        if (ElevationAnalysis is null || DataContext is not MainViewModel viewModel) return;
        ElevationAnalysis.SelectTarget(viewModel.MapContextProject,
            viewModel.SelectedAnalysisModel, viewModel.SelectedSplitSectionIds.Count);
    }

    private void OnOpenElevationAnalysis(object sender, RoutedEventArgs e) =>
        ShowBottomPanel(2);

    public async Task<bool> RunP8GpsUiSmokeAsync(HikingGuide.Core.Models.HikingProject project,
        HikingGuide.Core.Models.Track track)
    {
        BottomTabs.SelectedIndex = 2;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        return await ElevationAnalysis.RunAutomationGpsAsync(project, track);
    }

    public async Task<bool> RunP9TerrainUiSmokeAsync(HikingGuide.Core.Models.HikingProject project,
        HikingGuide.Core.Models.Track track)
    {
        BottomTabs.SelectedIndex = 2;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        return await ElevationAnalysis.RunAutomationTerrainAsync(project, track);
    }

    public Task<bool> RunP8NoGpsUiSmokeAsync(HikingGuide.Core.Models.HikingProject project,
        HikingGuide.Core.Models.Track track) =>
        ElevationAnalysis.RunAutomationNoGpsAsync(project, track);

    public void RefreshP8Sources() => ElevationAnalysis.RefreshSources();

    public Task<(bool GpsVsDem, bool DemVsDem)> RunP8ComparisonUiSmokeAsync(
        HikingGuide.Core.Models.HikingProject project, HikingGuide.Core.Models.Track track) =>
        ElevationAnalysis.RunAutomationComparisonsAsync(project, track);

    public Task<bool> RunP81SelectionUiSmokeAsync(HikingGuide.Core.Models.HikingProject project,
        IReadOnlyList<HikingGuide.Core.Models.Track> tracks) =>
        ElevationAnalysis.RunAutomationSelectionAsync(project, tracks);

    public (bool LeftZoom, bool RightZoom, bool LeftPan, bool RightPan,
        bool FullRange, bool Hover) RunP81ViewportUiSmoke() =>
        ElevationAnalysis.RunAutomationViewport();
    public double LastP81ViewportMilliseconds => ElevationAnalysis.LastViewportAutomationMilliseconds;

    private void OnOpenDemDatabases(object sender, RoutedEventArgs e) =>
        OpenDemDatabaseManager();

    public void OpenDemDatabaseManager()
    {
        if (_demDatabaseWindow is { IsVisible: true })
        {
            _demDatabaseWindow.Activate();
            return;
        }
        if (System.Windows.Application.Current is not App app) return;
        _demDatabaseWindow = new DemDatabaseWindow(app.DemCatalog) { Owner = this };
        _demDatabaseWindow.DatabasesChanged += (_, _) =>
        {
            ElevationAnalysis.RefreshSources();
            if (DataContext is MainViewModel viewModel &&
                ProjectTree.SelectedItem is NavigationNode selected)
                viewModel.SelectNavigationNode(selected, false);
        };
        _demDatabaseWindow.Show();
    }

    private void OnNavigationLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel ||
            FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<ToggleButton>(e.OriginalSource as DependencyObject) is not null) return;
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is NavigationNode node)
        {
            viewModel.UpdateProjectItemSelection(node, Keyboard.Modifiers);
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
                e.Handled = true;
        }
    }

    private void OnNavigationRightClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is FrameworkElement { DataContext: NavigationNode node })
        {
            viewModel.UpdateProjectItemSelection(node, ModifierKeys.None, preserveExisting: true);
            if (node.IsEditableGeometry)
                viewModel.SelectNavigationNode(node, false, preserveMultiSelection: true);
        }
    }

    private void OnNavigationTextClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: NavigationNode node } ||
            !node.IsEditableGeometry || DataContext is not MainViewModel viewModel) return;
        var command = viewModel.EditProjectGeometryItemCommand;
        if (command.CanExecute(node.Model)) command.Execute(node.Model);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private void OnGuideTableSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is DataGrid { SelectedItem: GuideTableRow row })
            viewModel.SelectNavigationNode(new NavigationNode(row.Name, row.Node));
    }

    private void OnGuideTableDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is DataGrid { SelectedItem: GuideTableRow row } &&
            viewModel.EditMarkerCommand.CanExecute(row.Node))
            viewModel.EditMarkerCommand.Execute(row.Node);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (e.Key == Key.Escape && viewModel.Map.IsFavoriteDragging)
        { e.Handled = true; _ = viewModel.Map.SendFavoriteCommandAsync("CancelFavoriteDrag"); return; }
        if (e.Key == Key.Escape && viewModel.IsTrackEditActive)
        {
            e.Handled = true;
            _ = viewModel.CancelPendingTrackEditAsync();
            return;
        }
        var delete = e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None;
        var history = e.Key is Key.Z or Key.Y && Keyboard.Modifiers == ModifierKeys.Control;
        if (!delete && !history) return;
        for (DependencyObject? focus = Keyboard.FocusedElement as DependencyObject; focus is not null; focus = VisualTreeHelper.GetParent(focus))
            if (focus is TextBoxBase or PasswordBox or ComboBox) return;
        if (delete)
        {
            var marker = viewModel.SelectedMarker;
            if (marker is null || !viewModel.DeleteMarkerCommand.CanExecute(marker)) return;
            viewModel.DeleteMarkerCommand.Execute(marker);
        }
        else
        {
            var command = e.Key == Key.Z ? viewModel.UndoCommand : viewModel.RedoCommand;
            if (!command.CanExecute(null)) return;
            command.Execute(null);
        }
        e.Handled = true;
    }

    private void OnManageMapSources(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || System.Windows.Application.Current is not App app) return;
        var dialog = new MapSourceManagerWindow(app.GlobalSettingsService, app.MapSourceTestService, app.MapSourceTemplateService) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            viewModel.Map.RefreshMapSources();
            if (dialog.CurrentSourceId is Guid id)
            {
                var source = viewModel.Map.MapSources.FirstOrDefault(value => value.Id == id);
                if (source is not null) viewModel.Map.SelectedMapSource = source;
            }
        }
    }
    internal void OpenMapManager()=>OnManageMapSources(this,new RoutedEventArgs());

    private void OnTerrain3DSettings(object sender,RoutedEventArgs e)
    {if(DataContext is MainViewModel vm)new Terrain3DWindow(vm.Map){Owner=this}.ShowDialog();}

    private void OnOpenManual(object sender, RoutedEventArgs e) => OpenManual(this);

    private void OnOpenAbout(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    internal static void OpenManual(Window owner)
    {

        const string manualFile = "易行徒步规划制作器-操作说明.html";
        var file = Path.Combine(AppContext.BaseDirectory, "docs", manualFile);
        if (!File.Exists(file))
        {
            MessageBox.Show(owner,
                "尚未找到操作说明。请确认软件目录中的“docs\\易行徒步规划制作器-操作说明.html”文件存在。",
                "操作说明暂不可用", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            (System.Windows.Application.Current as App)?.LoggingService.Error(exception, "打开离线操作说明失败");
            MessageBox.Show(owner,
                "无法打开操作说明。请检查说明文件是否可读取，以及 Windows 默认浏览器是否可以正常打开 HTML 文件。",
                "操作说明打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeApproved || DataContext is not MainViewModel viewModel) return;
        e.Cancel = true;
        if (_closeInProgress) return;
        _closeInProgress = true;
        var approved = false;
        try
        {
            if (!GuideWorkbench.CommitEdits()) return;
            GuideWorkbench.SuspendForClose();
            // 不在保存提示之前等待列宽写盘或生成完整的项目恢复归档。
            var closeDecision = viewModel.CanCloseApplicationAsync(GuideWorkbench.WaitForBackgroundUpdatesAsync);
            IsEnabled = false;
            if (!await closeDecision) return;
            await GuideWorkbench.FlushColumnWidthsAsync();
            await SavePanelLayoutSafelyAsync();
            approved = _closeApproved = true;
            await Dispatcher.InvokeAsync(Close, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception error)
        {
            ((App)System.Windows.Application.Current).LoggingService.Error(error, "关闭软件未完成，保留当前窗口");
            MessageBox.Show(this, "关闭未完成，项目窗口已保留。请先保存项目后重试。\n" + error.Message, "暂未关闭", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _closeInProgress = false; if (!approved) { IsEnabled = true; viewModel.ResumeAfterCancelledApplicationClose(); GuideWorkbench.ResumeAfterCancelledClose(); } }
    }
}

