using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows;
using HikingGuide.Application.Guides;
using HikingGuide.Application.Maps;
using HikingGuide.Application.Logging;
using HikingGuide.Application.Projects;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Application.TrackEditing;
using HikingGuide.App.Services;
using HikingGuide.Core.Models;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Services;
using HikingGuide.Infrastructure.TrackFiles;
using HikingGuide.Infrastructure.Media;
using HikingGuide.Application.Media;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.Elevation;

namespace HikingGuide.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IProjectService _projects;
    private readonly ILoggingService _log;
    private readonly ITrackFileService _trackFiles;
    private readonly IFileDialogService _dialogs;
    private readonly IRecentProjectService _recent;
    private readonly IAutoSaveService _autoSave;
    private readonly IActiveProjectService _activeProject;
    private readonly IExternalMediaDownloadService _externalMediaDownloader;
    private readonly ProjectMediaContentService _projectMedia = new();
    private readonly Dictionary<(Guid DatabaseId, string Fingerprint, double Latitude, double Longitude), string> _markerElevationCache = [];
    private long _markerElevationQueryVersion;
    private readonly ProjectThumbnailService _thumbnails = new(new ApplicationDataPaths());
    private readonly Dictionary<Guid, NavigationNode> _mapNodes = [];
    private readonly HashSet<object> _selectedProjectItems = [];
    private readonly HashSet<string> _selectedNavigationKeys = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _selectedSplitSectionIds = [];
    private Guid? _selectedSplitTrackId;
    private string? _selectionAnchorKey;
    private string? _cancelledPrimaryKey;
    private readonly List<string> _primaryCandidateKeys = [];
    private Track? _observedPrimaryTrack;
    private ProjectEditHistory _history = new();
    private readonly Dictionary<Guid, ProjectEditHistory> _histories = [];
    private readonly GuideStructureService _guideStructure = new();
    private readonly TrackEditService _trackEdits = new();
    private readonly TrackPointEditIndex _trackEditIndex = new();
    private readonly TrackRangePreviewService _trackRangePreview = new();
    private readonly TrackEditSessionState _trackEditState = new();
    private HikingProject? _trackEditProject => _trackEditState.Project;
    private Track? _trackEditTrack => _trackEditState.Track;
    private Guid? _trackEditSessionId => _trackEditState.SessionId;
    private Task _trackEditContextChangeTask = Task.CompletedTask;
    private bool _suppressTrackEditTargetSynchronization;
    private TrackPointEditPick? _trackEditPick;
    private TrackClickMode _trackClickMode;
    private TrackPosition? _trackRangeStart;
    private TrackPosition? _trackRangeEnd;
    private readonly AsyncRelayCommand _enterTrackEditCommand;
    private readonly AsyncRelayCommand _exitTrackEditCommand;
    private readonly RelayCommand<Track> _enterTrackEditContextCommand;
    private readonly RelayCommand<Track> _exitTrackEditContextCommand;
    private readonly AsyncRelayCommand _deleteTrackPointCommand;
    private readonly RelayCommand _addTrackPointCommand;
    private readonly RelayCommand<MapEventMessage> _splitTrackSegmentCommand;
    private readonly RelayCommand _selectTrackRangeCommand;
    private readonly AsyncRelayCommand _deleteTrackRangeCommand;
    private readonly AsyncRelayCommand _cropTrackRangeCommand;
    private readonly AsyncRelayCommand _reverseTrackCommand;
    private readonly AsyncRelayCommand _deleteTrackSegmentCommand;
    private readonly AsyncRelayCommand _reverseTrackSegmentCommand;
    private readonly AsyncRelayCommand _mergeAdjacentSegmentsCommand;
    private readonly AsyncRelayCommand _connectAdjacentSegmentsCommand;
    private readonly RelayCommand<SplitSection> _focusTrackSegmentCommand;
    private readonly AsyncRelayCommand _createMergedTrackCommand;
    private readonly AsyncRelayCommand _createConnectedTrackCommand;
    private HikingProject? _historyProject;
    private readonly SynchronizationContext? _uiContext;
    private readonly AsyncRelayCommand _newCommand;
    private readonly AsyncRelayCommand _openCommand;
    private readonly AsyncRelayCommand _saveCommand;
    private readonly AsyncRelayCommand _saveAsCommand;
    private readonly AsyncRelayCommand _closeCommand;
    private readonly RelayCommand<HikingProject> _closeProjectContextCommand;
    private readonly AsyncRelayCommand _importGpxCommand;
    private readonly AsyncRelayCommand _importKmlCommand;
    private readonly AsyncRelayCommand _importKmzCommand;
    private readonly AsyncRelayCommand _importUnifiedCommand;
    private readonly AsyncRelayCommand _exportGpxCommand;
    private readonly AsyncRelayCommand _exportKmlCommand;
    private readonly AsyncRelayCommand _exportKmzCommand;
    private readonly AsyncRelayCommand _mergedExportCommand;
    private readonly AsyncRelayCommand _resnapActiveGuidesCommand;
    private readonly RelayCommand<Track> _setActiveCommand;
    private readonly RelayCommand<Track> _setMainCommand;
    private readonly RelayCommand<Track> _clearMainCommand;
    private readonly RelayCommand<Track> _deleteTrackCommand;
    private readonly RelayCommand<Track> _setTrackColorCommand;
    private readonly RelayCommand<object> _editProjectGeometryItemCommand;
    private readonly RelayCommand _showSelectedTracksCommand;
    private readonly RelayCommand _hideSelectedTracksCommand;
    private readonly RelayCommand _recolorSelectedTracksCommand;
    private readonly RelayCommand _deleteSelectedProjectItemsCommand;
    private readonly RelayCommand _associateSelectedWaypointsCommand;
    private readonly RelayCommand _convertSelectedWaypointsCommand;
    private readonly RelayCommand _convertSelectedGuideNodesCommand;
    public ICommand BatchEditMarkersCommand { get; }
    public ICommand ChooseSnapBranchCommand {get;}
    private readonly RelayCommand<Track> _exportTrackGpxCommand;
    private readonly RelayCommand<object> _exportTrackKmlCommand;
    private readonly RelayCommand<object> _exportTrackKmzCommand;
    private readonly RelayCommand<Track> _viewTrackPropertiesCommand;
    private readonly RelayCommand<MapMarkerBase> _editMarkerCommand;
    private readonly RelayCommand<MapMarkerBase> _deleteMarkerCommand;
    private readonly RelayCommand<GuideNode> _pickGuidePositionCommand;
    private readonly RelayCommand _addWaypointMenuCommand;
    private readonly RelayCommand _addGuideMenuCommand;
    private readonly RelayCommand _editSelectedMenuCommand;
    private readonly RelayCommand _deleteSelectedMenuCommand;
    private readonly RelayCommand _convertToGuideMenuCommand;
    private readonly RelayCommand _convertToWaypointMenuCommand;
    private readonly RelayCommand _resnapSelectedMenuCommand;
    private readonly RelayCommand _pickSelectedMenuCommand;
    private readonly RelayCommand _associateCurrentTrackMenuCommand;
    private readonly RelayCommand _checkGuidesMenuCommand;
    private readonly RelayCommand _openMediaCommand;
    private object? _selectedModel;
    private bool _isMapSelectionInProgress;
    private bool _isRefreshingTree;
    private bool _suppressTreeSelection;
    private bool _suppressTreeRebuildFocus;
    private long _treeRebuildFocusVersion;
    private Task _mapRefreshTask = Task.CompletedTask;
    private readonly RelayCommand _cancelCommand;
    private Timer? _autoSaveTimer;
    private CancellationTokenSource? _operationCancellation;
    private string _statusText = "就绪";
    private string _progressMessage = "";
    private bool _isBusy;

    public MainViewModel(
        IProjectService projectService,
        ILoggingService loggingService,
        ITrackFileService trackFileService,
        IFileDialogService dialogService,
        IRecentProjectService recentProjectService,
        IAutoSaveService autoSaveService,
        IActiveProjectService activeProjectService,
        MapViewModel mapViewModel,
        IExternalMediaDownloadService externalMediaDownloader)
    {
        _projects = projectService;
        _log = loggingService;
        _trackFiles = trackFileService;
        _dialogs = dialogService;
        _recent = recentProjectService;
        _autoSave = autoSaveService;
        _activeProject = activeProjectService;
        _externalMediaDownloader = externalMediaDownloader;
        Map = mapViewModel;
        Map.SelectionRequested += OnMapSelectionRequested;
        Map.SplitSectionSelectionRequested += OnMapSplitSectionSelectionRequested;
        Map.MediaOpenRequested += OnMapMediaOpenRequested;
        Map.MediaThumbnailRequested += OnMapMediaThumbnailRequested;
        Map.MarkerEditRequested += OnMapMarkerEditRequested;
        Map.TrackEditRequested += OnMapTrackEditRequested;
        Map.RoutePlanningPointPicked += OnRoutePlanningPointPicked;
        Map.RoutePlanningUndoRequested += async(_,e)=>await UndoRoutePointFromMapAsync(e.ProjectId);
        Map.SnapLocationPicked += OnSnapLocationPicked;
        Map.UserMessage += (_, message) => StatusText = message;
        _uiContext = SynchronizationContext.Current;

        _newCommand = new AsyncRelayCommand(CreateNewAsync, () => !IsBusy);
        _openCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy);
        _saveCommand = new AsyncRelayCommand(() => SaveAsync(false), CanSave);
        _saveAsCommand = new AsyncRelayCommand(() => SaveAsync(true), CanSave);
        _closeCommand = new AsyncRelayCommand(() => CloseProjectAsync(), () => !IsBusy && CurrentProject is not null);
        _closeProjectContextCommand = new RelayCommand<HikingProject>(project => _ = CloseProjectAsync(project),
            project => !IsBusy && project is not null &&
                _projects.OpenProjects.Any(context => ReferenceEquals(context.Project, project)));
        _importGpxCommand = new AsyncRelayCommand(() => ImportAsync(TrackFileFormat.Gpx), () => !IsBusy);
        _importKmlCommand = new AsyncRelayCommand(() => ImportAsync(TrackFileFormat.Kml), () => !IsBusy);
        _importKmzCommand = new AsyncRelayCommand(() => ImportAsync(TrackFileFormat.Kmz), () => !IsBusy);
        _importUnifiedCommand = new AsyncRelayCommand(() => ImportAsync(TrackFileFormat.Unknown), () => !IsBusy);
        _exportGpxCommand = new AsyncRelayCommand(() => ExportAsync(TrackFileFormat.Gpx), CanExport);
        _exportKmlCommand = new AsyncRelayCommand(() => ExportAsync(TrackFileFormat.Kml), CanExport);
        _exportKmzCommand = new AsyncRelayCommand(() => ExportAsync(TrackFileFormat.Kmz), CanExport);
        _mergedExportCommand = new AsyncRelayCommand(MergedExportAsync,
            () => !IsBusy && CurrentProject is { } p && p.Tracks.Count + p.Waypoints.Count + p.GuideNodes.Count > 0);
        _resnapActiveGuidesCommand = new AsyncRelayCommand(ResnapActiveGuidesAsync,
            () => !IsBusy && CurrentProject?.MainGuideTrack is not null && CurrentProject.GuideNodes.Any(n => n.TrackId == CurrentProject.MainGuideTrackId));
        _setActiveCommand = new RelayCommand<Track>(SetActiveTrack, track => !IsBusy && track is not null);
        _setMainCommand = new RelayCommand<Track>(track => _ = ChangeMainTrackAsync(track),
            track => !IsBusy && track is not null && CurrentProject?.MainGuideTrackId != track.Id);
        _clearMainCommand = new RelayCommand<Track>(track => _ = ChangeMainTrackAsync(null),
            track => !IsBusy && track is not null && CurrentProject?.MainGuideTrackId == track.Id);
        _deleteTrackCommand = new RelayCommand<Track>(DeleteTrack,
            track => !IsBusy && track is not null && CurrentProject?.Tracks.Contains(track) == true);
        _setTrackColorCommand = new RelayCommand<Track>(SetTrackColor,
            track => !IsBusy && track is not null && CurrentProject?.Tracks.Contains(track) == true);
        _editProjectGeometryItemCommand = new RelayCommand<object>(EditProjectGeometryItem,
            model => !IsBusy && CurrentProject is { } project && model switch
            {
                Track track => project.Tracks.Contains(track),
                SplitSection section => project.Tracks.Any(track => track.Id == section.TrackId &&
                    track.GetSplitSections().Any(item => item.Id == section.Id)),
                GuideSegment segment => project.GuideSegments.Contains(segment),
                _ => false
            });
        BatchEditMarkersCommand = new RelayCommand(BatchEditMarkers, () => !IsBusy && CurrentProject is { } p &&
            p.Waypoints.Cast<MapMarkerBase>().Concat(p.GuideNodes).Any(_selectedProjectItems.Contains));
        _showSelectedTracksCommand = new RelayCommand(() => SetSelectedTrackVisibility(true), HasSelectedTracks);
        _hideSelectedTracksCommand = new RelayCommand(() => SetSelectedTrackVisibility(false), HasSelectedTracks);
        _recolorSelectedTracksCommand = new RelayCommand(RecolorSelectedTracks, HasSelectedTracks);
        _deleteSelectedProjectItemsCommand = new RelayCommand(DeleteSelectedProjectItems,
            () => !IsBusy && _selectedProjectItems.Count > 0);
        _associateSelectedWaypointsCommand = new RelayCommand(AssociateSelectedWaypoints,
            () => !IsBusy && CurrentProject?.ActiveTrack is not null &&
                CurrentProject.Waypoints.Any(_selectedProjectItems.Contains));
        _convertSelectedWaypointsCommand = new RelayCommand(() => _ = ConvertSelectedWaypointsAsync(),
            () => !IsBusy && CurrentProject?.MainGuideTrack is not null &&
                CurrentProject.Waypoints.Any(_selectedProjectItems.Contains));
        _convertSelectedGuideNodesCommand = new RelayCommand(() => _ = ConvertSelectedGuideNodesAsync(),
            () => !IsBusy && CurrentProject is { } project &&
                project.GuideNodes.Any(_selectedProjectItems.Contains));
        _exportTrackGpxCommand = new RelayCommand<Track>(track => _ = ExportTrackAsync(track, TrackFileFormat.Gpx), track => !IsBusy && track is not null);
        _exportTrackKmlCommand = new RelayCommand<object>(item => _ = ExportTrackAsync(item, TrackFileFormat.Kml), CanExportTreeTrack);
        _exportTrackKmzCommand = new RelayCommand<object>(item => _ = ExportTrackAsync(item, TrackFileFormat.Kmz), CanExportTreeTrack);
        _viewTrackPropertiesCommand = new RelayCommand<Track>(track =>
        {
            if (track is not null) SelectNavigationNode(new NavigationNode(track.Name ?? "未命名轨迹", track));
        }, track => track is not null);
        _editMarkerCommand = new RelayCommand<MapMarkerBase>(marker => _ = EditMarkerAsync(marker),
            marker => !IsBusy && marker is Waypoint or GuideNode);
        ChooseSnapBranchCommand = new RelayCommand<MapMarkerBase>(marker=> {if(marker is not null) _ = SnapMapMarkerAsync(marker,marker is Waypoint,true);},
            marker=>!IsBusy && marker is Waypoint or GuideNode);
        _deleteMarkerCommand = new RelayCommand<MapMarkerBase>(DeleteMarker,
            marker => !IsBusy && marker is Waypoint or GuideNode);
        _pickGuidePositionCommand = new RelayCommand<GuideNode>(node => _ = BeginGuidePositionPickAsync(node),
            node => !IsBusy && node?.TrackId is not null);
        _addWaypointMenuCommand = new RelayCommand(() => _ = BeginMarkerAddAsync(false), () => !IsBusy && CurrentProject is not null);
        _addGuideMenuCommand = new RelayCommand(() => _ = BeginMarkerAddAsync(true), () => !IsBusy && CurrentProject?.MainGuideTrack is not null);
        _editSelectedMenuCommand = new RelayCommand(() => _ = EditMarkerAsync(SelectedMarker), () => !IsBusy && SelectedMarker is not null);
        _deleteSelectedMenuCommand = new RelayCommand(() => DeleteMarker(SelectedMarker), () => !IsBusy && SelectedMarker is not null);
        _convertToGuideMenuCommand = new RelayCommand(() => _ = EditMarkerAsync(SelectedMarker, true),
            () => !IsBusy && SelectedMarker is Waypoint && CurrentProject?.MainGuideTrack is not null);
        _convertToWaypointMenuCommand = new RelayCommand(() => _ = EditMarkerAsync(SelectedMarker, false),
            () => !IsBusy && SelectedMarker is GuideNode);
        _resnapSelectedMenuCommand = new RelayCommand(() => _ = ResnapSelectedGuideAsync(),
            () => !IsBusy && SelectedMarker is GuideNode { TrackId: not null });
        _pickSelectedMenuCommand = new RelayCommand(() => _ = BeginGuidePositionPickAsync(SelectedMarker as GuideNode),
            () => !IsBusy && SelectedMarker is GuideNode { TrackId: not null });
        _associateCurrentTrackMenuCommand = new RelayCommand(() => _ = EditMarkerAsync(SelectedMarker, false),
            () => !IsBusy && SelectedMarker is Waypoint && CurrentProject?.Tracks.Count > 0);
        _checkGuidesMenuCommand = new RelayCommand(() => _ = CheckGuidesAsync(),
            () => !IsBusy && CurrentProject is not null);
        _openMediaCommand = new RelayCommand(OpenSelectedMedia,
            () => !IsBusy && SelectedMarker is { } marker && CurrentProject is { } project &&
                  (project.MediaAttachments.Any(item => item.OwnerId == marker.Id) ||
                   project.ExternalMediaReferences.Any(item => item.OwnerId == marker.Id)));
        _cancelCommand = new RelayCommand(() => _operationCancellation?.Cancel(), () => IsBusy && _operationCancellation is not null);
        _enterTrackEditCommand = new AsyncRelayCommand(EnterTrackEditAsync,
            () => !IsBusy && CurrentProject is not null && _trackEditSessionId is null &&
                !IsRoutePlanningActive);
        _exitTrackEditCommand = new AsyncRelayCommand(ExitTrackEditAsync,
            () => !IsBusy && _trackEditSessionId is not null);
        _enterTrackEditContextCommand = new RelayCommand<Track>(track =>
            { if (track is not null) _ = EnterTrackEditForAsync(track); },
            track => !IsBusy && track is not null && CurrentProject?.Tracks.Contains(track) == true &&
                !ReferenceEquals(_trackEditTrack, track) && !IsRoutePlanningActive);
        _exitTrackEditContextCommand = new RelayCommand<Track>(track => _ = ExitTrackEditAsync(),
            track => !IsBusy && track is not null && ReferenceEquals(_trackEditTrack, track));
        _deleteTrackPointCommand = new AsyncRelayCommand(DeleteSelectedTrackPointAsync,
            () => !IsBusy && !Map.TerrainCoordinateEditingBlocked&&_trackEditSessionId is not null && _trackEditPick is not null);
        _addTrackPointCommand = new RelayCommand(() => BeginTrackClickMode(TrackClickMode.InsertPoint),
            () => !IsBusy && !Map.TerrainCoordinateEditingBlocked&&_trackEditSessionId is not null);
        _splitTrackSegmentCommand = new RelayCommand<MapEventMessage>(value =>
        {
            if (_trackEditProject?.MainGuideTrackId == _trackEditTrack?.Id)
            {
                _dialogs.ShowWarning("不可拆分攻略轨迹", "攻略轨迹不可拆分，如要拆分请设为普通轨迹后，再操作。");
                return;
            }
            if (value is null) BeginTrackClickMode(TrackClickMode.SplitSegment);
            else _ = SplitTrackAtMapContextAsync(value);
        }, value => !IsBusy && !Map.TerrainCoordinateEditingBlocked&&_trackEditSessionId is not null &&
            _trackEditProject?.MainGuideTrackId != _trackEditTrack?.Id &&
            (value is null || IsCurrentTrackEdit(value)));
        _selectTrackRangeCommand = new RelayCommand(() => BeginTrackClickMode(TrackClickMode.SelectRange),
            () => !IsBusy && !Map.TerrainCoordinateEditingBlocked&&_trackEditSessionId is not null);
        _deleteTrackRangeCommand = new AsyncRelayCommand(() => EditSelectedRangeAsync(false),
            () => !IsBusy && !Map.TerrainCoordinateEditingBlocked&&_trackRangeStart is not null && _trackRangeEnd is not null);
        _cropTrackRangeCommand = new AsyncRelayCommand(() => EditSelectedRangeAsync(true),
            () => !IsBusy && !Map.TerrainCoordinateEditingBlocked&&_trackRangeStart is not null && _trackRangeEnd is not null);
        _reverseTrackCommand = new AsyncRelayCommand(() => EditCurrentTrackAsync("反转整条轨迹",
                token => _trackEdits.PrepareReverseTrack(_trackEditProject!, _trackEditTrack!.Id, token)),
            () => !IsBusy && !Map.TerrainCoordinateEditingBlocked&&_trackEditTrack is not null && _trackEditSessionId is not null);
        _deleteTrackSegmentCommand = new AsyncRelayCommand(() => EditSelectedSplitSectionAsync("删除轨迹段",
                (project, track, section, token) => _trackEdits.PrepareDeleteSplitSection(project, track.Id, section.Id, token)),
            CanEditSelectedSegment);
        _reverseTrackSegmentCommand = new AsyncRelayCommand(() => EditSelectedSplitSectionAsync("反转轨迹段",
                (project, track, section, token) => _trackEdits.PrepareReverseSplitSection(project, track.Id, section.Id, token)),
            CanEditSelectedSegment);
        _mergeAdjacentSegmentsCommand = new AsyncRelayCommand(() => CreateTrackFromTracksAsync(false), CanCombineGeometry);
        _connectAdjacentSegmentsCommand = new AsyncRelayCommand(() => CreateTrackFromTracksAsync(true), CanCombineGeometry);
        _focusTrackSegmentCommand = new RelayCommand<SplitSection>(segment =>
        {
            if (segment is not null) _ = Map.FocusObjectAsync(segment);
        }, segment => segment is not null);
        _createMergedTrackCommand = new AsyncRelayCommand(() => CreateTrackFromTracksAsync(false),
            CanCombineGeometry);
        _createConnectedTrackCommand = new AsyncRelayCommand(() => CreateTrackFromTracksAsync(true),
            CanCombineGeometry);
        UndoCommand = new RelayCommand(() => ChangeHistory(false), () => !IsBusy && _history.CanUndo);
        RedoCommand = new RelayCommand(() => ChangeHistory(true), () => !IsBusy && _history.CanRedo);
        _history.Changed += (_, _) => { ((RelayCommand)UndoCommand).RaiseCanExecuteChanged(); ((RelayCommand)RedoCommand).RaiseCanExecuteChanged(); };
        OpenRecentCommand = new RelayCommand<RecentProjectEntry>(entry => _ = OpenRecentAsync(entry), entry => !IsBusy && entry is not null);
        ExitCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty), () => !IsBusy);
        InitializeKilometerCommands();
        InitializeRoutingCommands();
        Map.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(MapViewModel.TerrainCoordinateEditingBlocked))RaiseCommandStates();};
    }

    public event EventHandler? CloseRequested;
    private HikingProject? CurrentProject => _projects.CurrentProject;
    public HikingProject? MapContextProject => CurrentProject;
    public Func<bool>? GuideEditsCommit { get; set; }
    private readonly Dictionary<Guid, CancellationTokenSource> _guideSaveDebounce = [];
    private readonly HashSet<Task> _guideSaveTasks = [];
    public void LocateGuideNode(Guid id)
    { if (_mapNodes.TryGetValue(id, out var node)) SelectNavigationNode(node, true); }
    public void QueueGuideAutoSave(HikingProject? project)
    {
        if (project is null || _applicationClosePending) return;
        if (_guideSaveDebounce.Remove(project.Id, out var old)) old.Cancel();
        var cancellation = new CancellationTokenSource(); _guideSaveDebounce[project.Id] = cancellation;
        var path = _projects.OpenProjects.FirstOrDefault(c => ReferenceEquals(c.Project, project))?.FilePath;
        var task = SaveGuideAfterPauseAsync(project, path, cancellation); _guideSaveTasks.Add(task);
        _ = RemoveCompletedGuideSaveAsync(task);
    }
    private async Task RemoveCompletedGuideSaveAsync(Task task)
    { try { await task; } finally { _guideSaveTasks.Remove(task); } }
    private async Task SaveGuideAfterPauseAsync(HikingProject project, string? path, CancellationTokenSource cancellation)
    {
        try { await Task.Delay(1200, cancellation.Token); await Task.Run(() => _autoSave.SaveIfDirtyAsync(project, path, cancellation.Token), cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _log.Error(error, "攻略数据自动保存失败"); StatusText = "攻略数据自动保存失败，请手动保存项目。"; }
        finally { if (_guideSaveDebounce.TryGetValue(project.Id, out var current) && ReferenceEquals(current, cancellation)) _guideSaveDebounce.Remove(project.Id); cancellation.Dispose(); }
    }
    public async Task FlushGuideAutoSaveAsync()
    {
        GuideEditsCommit?.Invoke();
        foreach (var token in _guideSaveDebounce.Values.ToArray()) token.Cancel();
        await Task.WhenAll(_guideSaveTasks.ToArray());
        foreach (var context in _projects.OpenProjects.ToArray()) await Task.Run(() => _autoSave.SaveIfDirtyAsync(context.Project, context.FilePath));
    }
    public string WindowTitle => CurrentProject is null
        ? HikingGuide.Core.Software.SoftwareIdentity.DisplayTitle
        : $"{HikingGuide.Core.Software.SoftwareIdentity.DisplayTitle} - {CurrentProject.Name}{(CurrentProject.IsDirty ? " *" : "")}";
    public string ProjectName => CurrentProject?.Name ?? "尚未打开项目";
    public ObservableCollection<NavigationNode> NavigationNodes { get; } = [];
    public int SelectedProjectItemCount => _selectedNavigationKeys.Count;
    public IReadOnlyCollection<Guid> SelectedSplitSectionIds => _selectedSplitSectionIds;
    public object? SelectedAnalysisModel => _selectedModel is SplitSection or GuideSegment ? _selectedModel : PrimarySelectedTrack;
    public Track? PrimarySelectedTrack => CurrentProject is { } project &&
        _projects.OpenProjects.FirstOrDefault(c => ReferenceEquals(c.Project, project))?.SelectedTrackId is Guid id
        ? project.Tracks.FirstOrDefault(t => t.Id == id) : null;
    public string CurrentSelectedTrackDisplayName => PrimarySelectedTrack?.Name ?? "无";
    public string CurrentSelectedTrackStatus => "当前选中轨迹：" + CurrentSelectedTrackDisplayName;
    private void NotifyPrimaryTrack()
    {
        var track = PrimarySelectedTrack;
        if (!ReferenceEquals(track, _observedPrimaryTrack))
        { if (_observedPrimaryTrack is not null) _observedPrimaryTrack.PropertyChanged -= OnPrimaryTrackChanged; _observedPrimaryTrack = track; if (track is not null) track.PropertyChanged += OnPrimaryTrackChanged; }
        OnPropertyChanged(nameof(PrimarySelectedTrack)); OnPropertyChanged(nameof(CurrentSelectedTrackDisplayName)); OnPropertyChanged(nameof(CurrentSelectedTrackStatus));
    }
    private void OnPrimaryTrackChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(Track.Name)) NotifyPrimaryTrack(); }
    public event EventHandler? AnalysisSelectionChanged;
    public event EventHandler? TrackColorChanged;
    public ObservableCollection<GuideTableRow> GuideRows { get; } = [];
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand EditMarkerCommand => _editMarkerCommand;
    public ICommand DeleteMarkerCommand => _deleteMarkerCommand;
    public ICommand PickGuidePositionCommand => _pickGuidePositionCommand;
    public ICommand ResnapActiveGuidesCommand => _resnapActiveGuidesCommand;
    public ICommand AddWaypointMenuCommand => _addWaypointMenuCommand;
    public ICommand AddGuideMenuCommand => _addGuideMenuCommand;
    public ICommand EditSelectedMenuCommand => _editSelectedMenuCommand;
    public ICommand DeleteSelectedMenuCommand => _deleteSelectedMenuCommand;
    public ICommand ConvertToGuideMenuCommand => _convertToGuideMenuCommand;
    public ICommand ConvertToWaypointMenuCommand => _convertToWaypointMenuCommand;
    public ICommand ResnapSelectedMenuCommand => _resnapSelectedMenuCommand;
    public ICommand PickSelectedMenuCommand => _pickSelectedMenuCommand;
    public ICommand AssociateCurrentTrackMenuCommand => _associateCurrentTrackMenuCommand;
    public ICommand CheckGuidesMenuCommand => _checkGuidesMenuCommand;
    public ICommand OpenMediaCommand => _openMediaCommand;
    public MapMarkerBase? SelectedMarker => _selectedModel as MapMarkerBase;
    public bool IsMapSelectionInProgress => _isMapSelectionInProgress;
    public ObservableCollection<PropertyRow> SelectedProperties { get; } = [];
    public ObservableCollection<RecentProjectEntry> RecentProjects { get; } = [];
    public MapViewModel Map { get; }
    public int MapSelectionSyncCount { get; private set; }

    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string ProgressMessage { get => _progressMessage; private set => SetField(ref _progressMessage, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetField(ref _isBusy, value)) RaiseCommandStates(); }
    }

    public ICommand NewProjectCommand => _newCommand;
    public ICommand OpenProjectCommand => _openCommand;
    public ICommand SaveProjectCommand => _saveCommand;
    public ICommand SaveProjectAsCommand => _saveAsCommand;
    public ICommand CloseProjectCommand => _closeCommand;
    public ICommand CloseProjectContextCommand => _closeProjectContextCommand;
    public ICommand ImportGpxCommand => _importGpxCommand;
    public ICommand ImportKmlCommand => _importKmlCommand;
    public ICommand ImportKmzCommand => _importKmzCommand;
    public ICommand ImportUnifiedCommand => _importUnifiedCommand;
    public ICommand ExportGpxCommand => _exportGpxCommand;
    public ICommand ExportKmlCommand => _exportKmlCommand;
    public ICommand ExportKmzCommand => _exportKmzCommand;
    public ICommand MergedExportCommand => _mergedExportCommand;
    public ICommand SetActiveTrackCommand => _setActiveCommand;
    public ICommand SetMainGuideTrackCommand => _setMainCommand;
    public ICommand ClearMainGuideTrackCommand => _clearMainCommand;
    public ICommand DeleteTrackCommand => _deleteTrackCommand;
    public ICommand SetTrackColorCommand => _setTrackColorCommand;
    public ICommand ShowSelectedTracksCommand => _showSelectedTracksCommand;
    public ICommand HideSelectedTracksCommand => _hideSelectedTracksCommand;
    public ICommand RecolorSelectedTracksCommand => _recolorSelectedTracksCommand;
    public ICommand EditProjectGeometryItemCommand => _editProjectGeometryItemCommand;
    public ICommand DeleteSelectedProjectItemsCommand => _deleteSelectedProjectItemsCommand;
    public ICommand AssociateSelectedWaypointsCommand => _associateSelectedWaypointsCommand;
    public ICommand ConvertSelectedWaypointsCommand => _convertSelectedWaypointsCommand;
    public ICommand ConvertSelectedGuideNodesCommand => _convertSelectedGuideNodesCommand;
    public ICommand ExportTrackGpxCommand => _exportTrackGpxCommand;
    public ICommand ExportTrackKmlCommand => _exportTrackKmlCommand;
    public ICommand ExportTrackKmzCommand => _exportTrackKmzCommand;
    public ICommand ViewTrackPropertiesCommand => _viewTrackPropertiesCommand;
    public ICommand OpenRecentCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand CancelOperationCommand => _cancelCommand;
    public ICommand EnterTrackEditCommand => _enterTrackEditCommand;
    public ICommand ExitTrackEditCommand => _exitTrackEditCommand;
    public ICommand EnterTrackEditContextCommand => _enterTrackEditContextCommand;
    public ICommand ExitTrackEditContextCommand => _exitTrackEditContextCommand;
    public ICommand DeleteTrackPointCommand => _deleteTrackPointCommand;
    public bool IsTrackEditActive => _trackEditSessionId is not null;
    internal bool AutoConfirmTrackEditForAutomation { get; set; }
    public Guid? SelectedTrackEditPointId => _trackEditPick?.SelectedPoint.PointId;
    public ICommand AddTrackPointCommand => _addTrackPointCommand;
    public ICommand SplitTrackSegmentCommand => _splitTrackSegmentCommand;
    public ICommand SelectTrackRangeCommand => _selectTrackRangeCommand;
    public ICommand DeleteTrackRangeCommand => _deleteTrackRangeCommand;
    public ICommand CropTrackRangeCommand => _cropTrackRangeCommand;
    public ICommand ReverseTrackCommand => _reverseTrackCommand;
    public ICommand DeleteTrackSegmentCommand => _deleteTrackSegmentCommand;
    public ICommand ReverseTrackSegmentCommand => _reverseTrackSegmentCommand;
    public ICommand MergeAdjacentSegmentsCommand => _mergeAdjacentSegmentsCommand;
    public ICommand ConnectAdjacentSegmentsCommand => _connectAdjacentSegmentsCommand;
    public ICommand FocusTrackSegmentCommand => _focusTrackSegmentCommand;
    public ICommand CreateMergedTrackCommand => _createMergedTrackCommand;
    public ICommand CreateConnectedTrackCommand => _createConnectedTrackCommand;

    public async Task InitializeAsync(bool previousSessionEndedNormally, bool restoreLastProject = true)
    {
        await RefreshRecentAsync();
        var previousOpenPaths = restoreLastProject ? await _activeProject.GetOpenPathsAsync() : [];
        var previousActivePath = restoreLastProject ? await _activeProject.GetAsync() : null;
        var candidates = await _autoSave.FindRecoveryCandidatesAsync(previousSessionEndedNormally);
        foreach (var candidate in candidates)
        {
            if (_dialogs.ConfirmRecovery(candidate) != RecoveryChoice.Recover) continue;
            await RunProjectOperationAsync("正在恢复项目……", async (_, token) =>
            {
                var project = await _autoSave.RecoverAsync(candidate, token);
                _projects.UseRecovered(project, candidate.FormalProjectPath);
                await RememberActiveProjectAsync(candidate.FormalProjectPath);
                RefreshProjectView();
                StatusText = $"已恢复项目：{project.Name}，请手动保存确认。";
            });
        }
        if (restoreLastProject)
        {
            try
            {
                // 工作区只在全部读取后发布一次，避免对每个中间项目重复构建地图/攻略/项目树。
                await RunProjectOperationAsync("正在恢复上次工作区……", async (progress, token) =>
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    ProjectBatchOpenResult? restored = null;
                    try
                    {
                        restored = await new ProjectBatchOpenService(_projects).OpenAsync(previousOpenPaths.Where(File.Exists), progress, token);
                    }
                    finally
                    {
                        LifecycleTimings["RestoreFilesMs"] = clock.Elapsed.TotalMilliseconds;
                        var active = _projects.OpenProjects.FirstOrDefault(item =>
                            item.FilePath?.Equals(previousActivePath, StringComparison.OrdinalIgnoreCase) == true)
                            ?? _projects.OpenProjects.LastOrDefault();
                        if (active is not null) _projects.Activate(active.ContextId);
                        await RememberActiveProjectAsync(_projects.CurrentProjectPath);
                        RefreshProjectView();
                        await RefreshRecentAsync();
                        LifecycleTimings["RestorePublishedMs"] = clock.Elapsed.TotalMilliseconds;
                    }
                    if (restored is { FailedCount: > 0 })
                        _dialogs.ShowWarning("部分项目恢复失败", string.Join("\n", restored.Items.Where(i => i.Error is not null).Select(i => $"{Path.GetFileName(i.FilePath)}：{i.Error}")));
                });
            }
            catch (Exception exception)
            {
                _log.Error(exception, "恢复上次打开的项目失败");
                StatusText = "恢复上次项目失败，可从最近项目手动打开。";
            }
        }
        _autoSaveTimer = new Timer(_ => _uiContext?.Post(_ => StartPeriodicAutoSave(), null), null, _autoSave.Interval, _autoSave.Interval);
    }

    private bool _applicationClosePending;
    private Task _periodicAutoSaveTask = Task.CompletedTask;
    private CancellationTokenSource? _periodicAutoSaveCancellation;
    public async Task<bool> CanCloseApplicationAsync(Func<Task>? finishBackgroundUpdates = null)
    {
        if (_applicationClosePending) return false;
        if (IsBusy) { _dialogs.ShowInformation("暂不能关闭", "当前项目操作尚未完成，请稍后再关闭，避免中断正在写入的数据。"); return false; }
        if (GuideEditsCommit?.Invoke() == false) return false;
        if (_routePlan?.Plan.ControlPoints.Count > 0 && !ConfirmDiscardRoutePlan()) return false;
        var original = _projects.OpenProjects.FirstOrDefault(context => ReferenceEquals(context.Project, CurrentProject))?.ContextId;
        var approved = false;
        _applicationClosePending = true;
        _autoSaveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _periodicAutoSaveCancellation?.Cancel();
        foreach (var token in _guideSaveDebounce.Values.ToArray()) token.Cancel();
        try
        {
            // 所有选择先收集：第一次await和任何磁盘写入之前就显示保存提示。
            var choices = new List<(OpenProjectContext Context, UnsavedChangesChoice Choice)>();
            foreach (var context in _projects.OpenProjects.ToArray())
            {
                if (!context.Project.IsDirty) continue;
                var choice = _dialogs.ConfirmUnsavedChanges(context.Project.Name);
                if (choice == UnsavedChangesChoice.Cancel) return false;
                choices.Add((context, choice));
            }
            if (finishBackgroundUpdates is not null) await finishBackgroundUpdates();
            // 结束已有的恢复文件写入后再正式保存，避免媒体引用和保存任务交叉。
            await Task.WhenAll(_guideSaveTasks.ToArray().Append(_periodicAutoSaveTask));
            foreach (var (context, choice) in choices)
            {
                if (choice != UnsavedChangesChoice.Save) continue;
                _projects.Activate(context.ContextId);
                var path = context.FilePath ?? _dialogs.ShowSaveProjectDialog(context.Project.Name);
                if (path is null || !await SaveToPathAsync(path)) return false;
            }
            CancelRoutePlan(false);
            approved = true;
            return true;
        }
        finally
        {
            if (original is Guid id) _projects.Activate(id);
            if (!approved)
            {
                _applicationClosePending = false;
                _autoSaveTimer?.Change(_autoSave.Interval, _autoSave.Interval);
                foreach (var context in _projects.OpenProjects.Where(c => c.Project.IsDirty)) QueueGuideAutoSave(context.Project);
                RefreshProjectView();
            }
        }
    }

    public Task OpenForAutomationAsync(string path) => OpenPathAsync(path);
    internal Task CloseForAutomationAsync() => CloseProjectAsync();
    public Task WaitForMapRefreshForAutomationAsync() => _mapRefreshTask;

    internal void RefreshProjectTreeForAutomation() => RefreshProjectView(false);
    internal void RefreshWorkspaceForAutomation() => RefreshProjectView();

    internal void RunMapMarkerSelectionForAutomation(Guid markerId) =>
        OnMapSelectionRequested(this, new MapSelection(MapSelectionKind.Waypoint, markerId));

    private long _navigationSelectionRequestVersion;
    public void SelectNavigationNode(NavigationNode? node, bool focusOnMap = true,
        bool preserveMultiSelection = false)
    {
        if (_suppressTreeSelection || _isRefreshingTree) return;
        if (GuideEditsCommit?.Invoke() == false) return;
        if (node is not null && node.SelectionKey == _cancelledPrimaryKey) return;
        var selectionRequestVersion=++_navigationSelectionRequestVersion;
        // 地图点选同步树节点可能再次触发 WPF SelectedItemChanged，不允许该回调适应全轨迹。
        focusOnMap &= !_isMapSelectionInProgress && !_isRefreshingTree && !_suppressTreeRebuildFocus;
        var switchedProject = false;
        if (node?.Model is { } model)
        {
            var owner = _projects.OpenProjects.FirstOrDefault(context =>
                ReferenceEquals(context.Project, model) || context.Project.Tracks.Any(item => ReferenceEquals(item, model)) ||
                context.Project.Waypoints.Any(item => ReferenceEquals(item, model)) ||
                context.Project.GuideNodes.Any(item => ReferenceEquals(item, model)) ||
                context.Project.GuideSegments.Any(item => ReferenceEquals(item, model)) ||
                model is SplitSection split && context.Project.Tracks.Any(track => track.Id == split.TrackId) ||
                context.Project.Tracks.SelectMany(track => track.Segments).Any(item => ReferenceEquals(item, model)));
            if (owner is not null && !ReferenceEquals(CurrentProject, owner.Project))
            {
                // Rebuild restores selection from the owning context. Record the user's
                // target first, rather than restoring that project's previous selection.
                owner.SelectedObjectId=model switch
                {
                    HikingProject p=>p.Id,Track t=>t.Id,Waypoint w=>w.Id,GuideNode g=>g.Id,
                    GuideSegment g=>g.Id,SplitSection part=>part.Id,_=>null
                };
                _projects.Activate(owner.ContextId);
                RefreshProjectView();
                switchedProject = true;
            }
            if (CurrentProject is { } selectedProject)
            {
                if (model is Track && !preserveMultiSelection) ClearSplitSectionSelection();
                var related = model is HikingProject ? owner?.SelectedTrackId : PrimaryTrackSelection.Resolve(selectedProject, model);
                if (related.HasValue && !selectedProject.Tracks.Any(t => t.Id == related)) related = null;
                selectedProject.ActiveTrackId = related;
                if (owner is not null) owner.SelectedTrackId = related;
            }
            if (owner is not null) owner.SelectedObjectId = model switch
            {
                HikingProject project => project.Id, Track track => track.Id,
                Waypoint point => point.Id, GuideNode guide => guide.Id,
                GuideSegment segment => segment.Id, SplitSection section => section.Id,
                _ => null
            };
        }
        else if (CurrentProject is { } noSelectionProject)
        { noSelectionProject.ActiveTrackId = null; var context = _projects.OpenProjects.First(c => ReferenceEquals(c.Project, noSelectionProject)); context.SelectedTrackId = null; context.SelectedObjectId = null; }
        _selectedModel = node?.Model;
        SelectedProperties.Clear();
        _markerElevationQueryVersion++;
        switch (node?.Model)
        {
            case HikingProject project:
                SelectedProperties.Add(new("项目名称", project.Name));
                SelectedProperties.Add(new("项目 ID", project.Id.ToString()));
                SelectedProperties.Add(new("项目版本", project.ProjectVersion.ToString(CultureInfo.CurrentCulture)));
                SelectedProperties.Add(new("轨迹数量", project.Tracks.Count.ToString(CultureInfo.CurrentCulture)));
                SelectedProperties.Add(new("项目文件", _projects.OpenProjects.FirstOrDefault(item => ReferenceEquals(item.Project, project))?.FilePath ?? "尚未正式保存"));
                break;
            case Track track:
                SelectedProperties.Add(new("名称", track.Name ?? "未命名轨迹"));
                SelectedProperties.Add(new("轨迹 ID", track.Id.ToString()));
                SelectedProperties.Add(new("当前选中轨迹", track.Id == CurrentProject?.ActiveTrackId ? "是" : "否"));
                SelectedProperties.Add(new("主攻略轨迹", track.Id == CurrentProject?.MainGuideTrackId ? "是" : "否"));
                SelectedProperties.Add(new("显示颜色", track.DisplayColor ?? "默认蓝色"));
                SelectedProperties.Add(new("轨迹段数量", track.Segments.Count.ToString(CultureInfo.CurrentCulture)));
                SelectedProperties.Add(new("轨迹点数量", track.GetPointCount().ToString(CultureInfo.CurrentCulture)));
                SelectedProperties.Add(new("总长度", $"{track.GetTotalLength() / 1000:F2} km"));
                SelectedProperties.Add(new("说明", track.Description ?? "—"));
                SelectedProperties.Add(new("原始来源", track.SourceFile ?? "无"));
                break;
            case TrackSegment segment:
                SelectedProperties.Add(new("名称", segment.Name ?? "未命名轨迹段"));
                SelectedProperties.Add(new("轨迹点数量", segment.PointCount.ToString(CultureInfo.CurrentCulture)));
                break;
            case SplitSection section:
                SelectedProperties.Add(new("名称", section.Name));
                SelectedProperties.Add(new("连续几何 Part", section.GeometryParts.Count.ToString(CultureInfo.CurrentCulture)));
                SelectedProperties.Add(new("轨迹点数量", section.PointCount.ToString(CultureInfo.CurrentCulture)));
                SelectedProperties.Add(new("实际长度", $"{section.Length / 1000:F2} km"));
                SelectedProperties.Add(new("说明", section.GeometryParts.FirstOrDefault()?.SplitSectionDescription ?? "—"));
                break;
            case Waypoint waypoint:
                SelectedProperties.Add(new("名称", waypoint.Name ?? "未命名标注"));
                SelectedProperties.Add(new("类型", MarkerTypeNames.Get(waypoint.Type, waypoint.CustomTypeName)));
                SelectedProperties.Add(new("自定义类型", waypoint.CustomTypeName ?? "—"));
                SelectedProperties.Add(new("纬度", waypoint.Latitude.ToString("F8", CultureInfo.InvariantCulture)));
                SelectedProperties.Add(new("经度", waypoint.Longitude.ToString("F8", CultureInfo.InvariantCulture)));
                SelectedProperties.Add(new("GPS海拔", waypoint.SourceElevation.HasValue ? $"{waypoint.SourceElevation.Value:F1} m" : "无数据"));
                SelectedProperties.Add(new("描述", waypoint.Description ?? "—"));
                if(waypoint.PreferredTrackPosition is { } selectedPosition)
                    SelectedProperties.Add(new("已选路段里程", $"距起点 {selectedPosition.DistanceFromTrackStart/1000:F3} km"));
                break;
            case GuideNode guideNode:
                SelectedProperties.Add(new("名称", guideNode.Name ?? "未命名攻略节点"));
                SelectedProperties.Add(new("所属轨迹", guideNode.TrackId?.ToString() ?? "尚未关联"));
                SelectedProperties.Add(new("来源轨迹", guideNode.SourceTrackName ??
                    CurrentProject?.Tracks.FirstOrDefault(track => track.Id == guideNode.SourceTrackId)?.Name ?? "—"));
                SelectedProperties.Add(new("类型", MarkerTypeNames.Get(guideNode.Type, guideNode.CustomTypeName)));
                SelectedProperties.Add(new("自定义类型", guideNode.CustomTypeName ?? "—"));
                SelectedProperties.Add(new("状态", guideNode.IsOrphaned ? "失联" : "正常"));
                SelectedProperties.Add(new("原始坐标", $"{guideNode.OriginalCoordinate.Latitude:F8}, {guideNode.OriginalCoordinate.Longitude:F8}"));
                SelectedProperties.Add(new("吸附坐标", guideNode.SnappedCoordinate is { } snappedCoordinate ? $"{snappedCoordinate.Latitude:F8}, {snappedCoordinate.Longitude:F8}" : "—"));
                SelectedProperties.Add(new("距起点", guideNode.DistanceFromStart is double distance ? $"{distance / 1000:F2} km" : "—"));
                SelectedProperties.Add(new("距轨迹", guideNode.DistanceToTrack is double offset ? $"{offset:F1} m" : "—"));
                SelectedProperties.Add(new("GPS海拔", guideNode.SourceElevation is double altitude ? $"{altitude:F1} m" : "无数据"));
                break;
            case GuideSegment guideSegment:
                SelectedProperties.Add(new("名称", GuideSegmentDisplayFormatter.Format(CurrentProject!, guideSegment)));
                SelectedProperties.Add(new("起点", CurrentProject?.GuideNodes.FirstOrDefault(n => n.Id == guideSegment.StartNodeId)?.Name ?? "—"));
                SelectedProperties.Add(new("终点", CurrentProject?.GuideNodes.FirstOrDefault(n => n.Id == guideSegment.EndNodeId)?.Name ?? "—"));
                SelectedProperties.Add(new("分段距离", $"{guideSegment.Distance / 1000:F2} km"));
                SelectedProperties.Add(new("起点累计距离", $"{guideSegment.StartDistance / 1000:F2} km"));
                SelectedProperties.Add(new("终点累计距离", $"{guideSegment.EndDistance / 1000:F2} km"));
                SelectedProperties.Add(new("路况", guideSegment.ManualData?.RoadCondition ?? "—"));
                SelectedProperties.Add(new("攻略描述", guideSegment.ManualData?.Description ?? "—"));
                SelectedProperties.Add(new("计划用时", guideSegment.ManualData?.PlannedDuration is { } planned
                    ? $"{planned.TotalMinutes:F0} 分钟" : "—"));
                break;
        }
        if (_selectedModel is MapMarkerBase marker && CurrentProject is { } mediaProject)
        {
            var internalCount = mediaProject.MediaAttachments.Count(item => item.OwnerId == marker.Id);
            var external = mediaProject.ExternalMediaReferences.Where(item => item.OwnerId == marker.Id).ToArray();
            SelectedProperties.Add(new("项目图片", $"{internalCount} 张"));
            SelectedProperties.Add(new("网络图片", $"{external.Length} 张"));
            foreach (var reference in external)
            {
                var host = Uri.TryCreate(reference.Url, UriKind.Absolute, out var url) ? url.Host : "未知来源";
                SelectedProperties.Add(new("网络来源", host));
                SelectedProperties.Add(new("图片URL（可复制）", reference.Url));
            }
            _ = QueryMarkerElevationsAsync(marker, mediaProject, _markerElevationQueryVersion);
        }
        if (switchedProject)
        {
            var selected = node?.Model;
            var expectedProject = CurrentProject;
            _ = SelectAfterProjectSwitchAsync(selected, expectedProject, focusOnMap,selectionRequestVersion);
        }
        else _ = Map.SelectObjectAsync(node?.Model, focusOnMap);
        if (!switchedProject && !_suppressTrackEditTargetSynchronization &&
            _trackEditState.IsActive && CurrentProject is { } editingProject)
        {
            var targetId = node?.Model is Track or SplitSection or TrackSegment
                ? PrimaryTrackSelection.Resolve(editingProject, node.Model) : null;
            if (targetId is Guid id && editingProject.Tracks.FirstOrDefault(track => track.Id == id) is { } target &&
                !ReferenceEquals(target, _trackEditTrack))
                _trackEditContextChangeTask = ActivateTrackEditTargetAsync(editingProject, target);
        }
        RaiseCommandStates();
        RaiseMarkerMenuStates();
        NotifyPrimaryTrack();
        AnalysisSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task QueryMarkerElevationsAsync(MapMarkerBase marker, HikingProject project,
        long queryVersion)
    {
        if (System.Windows.Application.Current is not App app) return;
        var elevationIndex = SelectedProperties.ToList().FindIndex(row => row.Name == "GPS海拔") + 1;
        if (elevationIndex == 0) return;
        if (app.DemProvider is null)
        {
            foreach (var database in app.DemCatalog.Databases)
                SelectedProperties.Insert(elevationIndex++, new($"{database.Name} 海拔",
                    app.DemStartupError ?? "DEM运行库不可用"));
            return;
        }
        var coordinate = marker switch
        {
            GuideNode node when !node.IsOrphaned && node.SnappedCoordinate.HasValue =>
                node.SnappedCoordinate.Value,
            GuideNode node => node.OriginalCoordinate,
            Waypoint point => point.Coordinate,
            _ => default
        };
        if (marker is GuideNode guide)
            SelectedProperties.Add(new("DEM查询位置", !guide.IsOrphaned &&
                guide.SnappedCoordinate.HasValue ? "轨迹吸附坐标" : "原始坐标"));
        foreach (var database in app.DemCatalog.Databases)
        {
            var label = $"{database.Name} 海拔";
            if (!Directory.Exists(database.RootPath))
            {
                SelectedProperties.Insert(elevationIndex++, new(label, "数据库未连接"));
                continue;
            }
            var key = (database.Id, database.Fingerprint, coordinate.Latitude, coordinate.Longitude);
            if (_markerElevationCache.TryGetValue(key, out var cached))
            {
                SelectedProperties.Insert(elevationIndex++, new(label, cached));
                continue;
            }
            SelectedProperties.Insert(elevationIndex++, new(label, "正在读取…"));
            string value;
            try
            {
                var result = await Task.Run(() => app.DemProvider.SampleDetailed(database.Id,
                    coordinate));
                value = result.Status switch
                {
                    DemSampleStatus.Valid => $"{result.ElevationMeters:F1} m",
                    DemSampleStatus.DatabaseDisconnected => "数据库未连接",
                    DemSampleStatus.NoCoverage => "无覆盖",
                    DemSampleStatus.InvalidUnit => "高程单位未确认",
                    _ => result.QualityFlag ?? "该位置无有效高程"
                };
            }
            catch (Exception error) { value = $"读取失败：{error.Message}"; }
            if (queryVersion != _markerElevationQueryVersion ||
                !ReferenceEquals(CurrentProject, project) || !ReferenceEquals(_selectedModel, marker)) return;
            _markerElevationCache[key] = value;
            var index = SelectedProperties.ToList().FindIndex(row => row.Name == label);
            if (index >= 0) SelectedProperties[index] = new PropertyRow(label, value);
        }
    }

    private async Task SelectAfterProjectSwitchAsync(object? selected, HikingProject? expectedProject, bool focus,long selectionVersion)
    {
        if (!ReferenceEquals(CurrentProject, expectedProject)||selectionVersion!=_navigationSelectionRequestVersion) return;
        var path = FindNavigationPath(selected);
        if (path.Count > 0)
        {
            _suppressTreeSelection = true;
            try
            {
                foreach (var ancestor in path.Take(path.Count - 1)) ancestor.IsExpanded = true;
                path[^1].IsSelected = true;
            }
            finally { _suppressTreeSelection = false; }
        }
        await _mapRefreshTask;
        if (!ReferenceEquals(CurrentProject, expectedProject)||selectionVersion!=_navigationSelectionRequestVersion) return;
        await Map.SelectObjectAsync(selected, focus);
    }

    private List<NavigationNode> FindNavigationPath(object? model)
    {
        foreach (var root in NavigationNodes)
        {
            var path = new List<NavigationNode>();
            if (Find(root, path)) return path;
        }
        return [];

        bool Find(NavigationNode node, List<NavigationNode> path)
        {
            path.Add(node);
            if (ReferenceEquals(node.Model, model)) return true;
            foreach (var child in node.Children)
                if (Find(child, path)) return true;
            path.RemoveAt(path.Count - 1);
            return false;
        }
    }

    private async Task CreateNewAsync()
    {
        if (GuideEditsCommit?.Invoke() == false) return;
        await FlushGuideAutoSaveAsync();
        var request = _dialogs.ShowNewProjectDialog();
        if (request is null) return;
        var project = _projects.CreateNew(request.Name);
        await RememberActiveProjectAsync(null);
        project.MarkDirty();
        RefreshProjectView();
        if (!await SaveToPathAsync(request.FilePath)) StatusText = "新项目已建立，但尚未保存。";
        else StatusText = $"已创建项目：{project.Name}";
    }

    private async Task OpenAsync()
    {
        if (GuideEditsCommit?.Invoke() == false) return;
        await FlushGuideAutoSaveAsync();
        var paths = _dialogs.ShowOpenProjectDialog();
        if (paths.Count == 0) return;
        await RunProjectOperationAsync("正在批量打开项目……", async (progress, token) =>
        {
            var result = await new ProjectBatchOpenService(_projects).OpenAsync(paths, progress, token);
            var failures = result.Items.Where(item => item.Error is not null)
                .Select(item => $"{Path.GetFileName(item.FilePath)}：{item.Error}").ToArray();
            foreach (var item in result.Items.Where(item => item.Error is not null))
                _log.Warning($"打开项目失败：{item.FilePath}；{item.Error}");
            await RememberActiveProjectAsync(_projects.CurrentProjectPath);
            RefreshProjectView();
            await RefreshRecentAsync();
            StatusText = $"打开成功 {result.OpenedCount} 个，已打开跳过 {result.AlreadyOpenCount} 个，失败 {result.FailedCount} 个。";
            if (failures.Length > 0) _dialogs.ShowWarning("批量打开项目", StatusText + "\n" + string.Join("\n", failures));
        });
    }

    private async Task OpenRecentAsync(RecentProjectEntry? entry)
    {
        if (entry is null) return;
        if (!File.Exists(entry.FilePath))
        {
            if (_dialogs.ConfirmRemoveMissingRecent(entry.FilePath))
            {
                await _recent.RemoveAsync(entry.FilePath);
                await RefreshRecentAsync();
            }
            return;
        }
        await OpenPathAsync(entry.FilePath);
    }

    private async Task<bool> OpenPathAsync(string path)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (GuideEditsCommit?.Invoke() == false) return false;
        await FlushGuideAutoSaveAsync();
        LifecycleTimings["OpenFlushMs"] = clock.Elapsed.TotalMilliseconds;
        var opened = false;
        await RunProjectOperationAsync("正在打开项目……", async (progress, token) =>
        {
            var project = await Task.Run(() => _projects.OpenAsync(path, progress, token), token);
            LifecycleTimings["OpenFileMs"] = clock.Elapsed.TotalMilliseconds;
            opened = true;
            await RememberActiveProjectAsync(_projects.CurrentProjectPath);
            RefreshProjectView();
            LifecycleTimings["OpenViewMs"] = clock.Elapsed.TotalMilliseconds;
            await RefreshRecentAsync();
            StatusText = $"已打开项目：{project.Name}";
        });
        return opened;
    }

    private async Task<bool> SaveAsync(bool saveAs)
    {
        if (GuideEditsCommit?.Invoke() == false) return false;
        var project = CurrentProject;
        if (project is null) return false;
        var path = saveAs || _projects.CurrentProjectPath is null
            ? _dialogs.ShowSaveProjectDialog(project.Name)
            : _projects.CurrentProjectPath;
        return path is not null && await SaveToPathAsync(path);
    }

    private async Task<bool> SaveToPathAsync(string path)
    {
        var succeeded = false;
        await RunProjectOperationAsync("正在保存项目……", async (progress, token) =>
        {
            await Task.Run(() => _projects.SaveAsAsync(path, progress, token), token);
            await RememberActiveProjectAsync(_projects.CurrentProjectPath);
            succeeded = true;
            StatusText = $"项目已保存：{Path.GetFileName(path)}";
            RefreshProjectView(false);
            await RefreshRecentAsync();
        });
        return succeeded;
    }

    private async Task CloseProjectAsync(HikingProject? targetProject = null)
    {
        if (GuideEditsCommit?.Invoke() == false) return;
        await FlushGuideAutoSaveAsync();
        var target = targetProject ?? CurrentProject;
        if (target is not null && _routePlan?.Plan.ProjectId == target.Id)
        {
            if (_routePlan.Plan.ControlPoints.Count > 0 && !ConfirmDiscardRoutePlan()) return;
            CancelRoutePlan(false);
        }
        var closedContext = _projects.OpenProjects.FirstOrDefault(item => ReferenceEquals(item.Project, target));
        if (closedContext is null) return;
        var previousContext = _projects.OpenProjects.FirstOrDefault(item => ReferenceEquals(item.Project, CurrentProject));
        if (!ReferenceEquals(CurrentProject, target)) _projects.Activate(closedContext.ContextId);
        if (!await ConfirmCanAbandonCurrentAsync())
        {
            if (previousContext is not null) _projects.Activate(previousContext.ContextId);
            RefreshProjectView();
            return;
        }
        _projects.Close();
        _histories.Remove(closedContext.ContextId);
        if (previousContext is not null && !ReferenceEquals(previousContext, closedContext))
            _projects.Activate(previousContext.ContextId);
        await RememberActiveProjectAsync(_projects.CurrentProjectPath);
        RefreshProjectView();
        StatusText = $"项目已关闭：{target?.Name}";
    }

    private async Task RememberActiveProjectAsync(string? path)
    {
        try
        {
            var openPaths = _projects.OpenProjects.Where(item => item.FilePath is not null)
                .Select(item => item.FilePath!).ToArray();
            await _activeProject.SetWorkspaceAsync(openPaths, path ?? _projects.CurrentProjectPath);
        }
        catch (Exception exception) { _log.Error(exception, "记录当前项目状态失败"); }
    }

    private async Task<bool> ConfirmCanAbandonCurrentAsync()
    {
        var project = CurrentProject;
        if (project is null || !project.IsDirty) return true;
        return _dialogs.ConfirmUnsavedChanges(project.Name) switch
        {
            UnsavedChangesChoice.Discard => true,
            UnsavedChangesChoice.Save => await SaveAsync(false),
            _ => false
        };
    }

    private async Task ImportAsync(TrackFileFormat format)
    {
        if (CurrentProject is null)
        {
            await CreateNewAsync();
            if (CurrentProject is null) return;
        }
        var paths = _dialogs.ShowOpenDialog(format);
        if (paths.Count == 0) return;
        await RunTrackOperationAsync("正在导入轨迹……", async (progress, token) =>
        {
            var project = CurrentProject!;
            var result = await Task.Run(() => new ProjectImportBatch(_trackFiles).ReadAsync(paths, project, progress, token,
                format == TrackFileFormat.Unknown ? null : format), token);
            if (!result.IsSuccess)
            {
                _dialogs.ShowError("导入失败", string.Join(Environment.NewLine, result.Errors.Select(error => error.Message)));
                return;
            }
            var importService = new ProjectImportService();
            var session = ProjectImportSession.Create(result, string.Join("; ", paths), project);
            await Task.Run(() => importService.Preview(session, project), token);
            var window = new ImportWindow(session, project, importService, _externalMediaDownloader) { Owner = System.Windows.Application.Current.MainWindow };
            if (window.ShowDialog() != true)
            {
                StatusText = "已取消导入，项目未修改";
                return;
            }
            var plan = await Task.Run(() => importService.Prepare(session, project), token);
            var warningDistance = project.Settings.GuideSnap.AutoSnapWarningDistanceMeters;
            var farCount = plan.GuideNodes.Count(node => node.DistanceToTrack > warningDistance);
            var confirmed = farCount == 0 || MessageBox.Show(window.Owner,
                $"有 {farCount} 个攻略节点距轨迹超过 {warningDistance:F0} 米。仍要按当前选择导入吗？",
                "确认较远攻略节点", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!confirmed)
            {
                StatusText = "已取消导入，项目未修改";
                return;
            }
            if (plan.Tracks.Count == 0 && plan.Waypoints.Count == 0 && plan.GuideNodes.Count == 0) return;
            var previousActiveTrackId = project.ActiveTrackId;
            var previousMainTrackId = project.MainGuideTrackId;
            var previousSegments = project.GuideSegments.ToArray();
            var previousRetiredSegments = project.RetiredGuideSegments.ToArray();
            GuideSegment[]? importedSegments = null;
            GuideSegment[]? importedRetiredSegments = null;
            _history.Execute("导入轨迹与标注", () =>
            {
                if (importedSegments is null)
                {
                    importService.Apply(plan, project, confirmed);
                    importedSegments = project.GuideSegments.ToArray();
                    importedRetiredSegments = project.RetiredGuideSegments.ToArray();
                }
                else project.ExecuteAtomicUpdate(p =>
                {
                    foreach (var track in plan.Tracks) p.AddTrack(track);
                    foreach (var waypoint in plan.Waypoints) p.Waypoints.Add(waypoint);
                    foreach (var node in plan.GuideNodes) p.GuideNodes.Add(node);
                    foreach (var blob in plan.MediaBlobs) p.MediaBlobs.Add(blob);
                    foreach (var attachment in plan.MediaAttachments) p.MediaAttachments.Add(attachment);
                    foreach (var reference in plan.ExternalMediaReferences) p.ExternalMediaReferences.Add(reference);
                    p.GuideSegments.Clear();
                    foreach (var segment in importedSegments) p.GuideSegments.Add(segment);
                    p.RetiredGuideSegments.Clear();
                    foreach (var segment in importedRetiredSegments!) p.RetiredGuideSegments.Add(segment);
                    p.MainGuideTrackId = plan.GuideTrackId;
                });
            }, () => project.ExecuteAtomicUpdate(p =>
            {
                foreach (var node in plan.GuideNodes) p.GuideNodes.Remove(node);
                foreach (var waypoint in plan.Waypoints) p.Waypoints.Remove(waypoint);
                foreach (var attachment in plan.MediaAttachments) p.MediaAttachments.Remove(attachment);
                foreach (var reference in plan.ExternalMediaReferences) p.ExternalMediaReferences.Remove(reference);
                foreach (var blob in plan.MediaBlobs) p.MediaBlobs.Remove(blob);
                p.GuideSegments.Clear();
                foreach (var segment in previousSegments) p.GuideSegments.Add(segment);
                p.RetiredGuideSegments.Clear();
                foreach (var segment in previousRetiredSegments) p.RetiredGuideSegments.Add(segment);
                foreach (var track in plan.Tracks) p.Tracks.Remove(track);
                p.ActiveTrackId = previousActiveTrackId;
                p.MainGuideTrackId = previousMainTrackId;
            }));
            RefreshProjectView();
            StatusText = $"已导入：轨迹 {plan.Tracks.Count} 条，普通标注 {plan.Waypoints.Count} 个，攻略节点 {plan.GuideNodes.Count} 个";
            _log.Information($"[ImportSession] {StatusText}；攻略分段 {project.GuideSegments.Count} 段");
            _dialogs.ShowInformation("导入完成", StatusText);
        });
    }

    private async Task ExportAsync(TrackFileFormat format)
    {
        var project = CurrentProject;
        if (project is null) return;
        var active = project.ActiveTrack;
        if (format == TrackFileFormat.Kml && project.MediaAttachments.Any(item =>
            project.Waypoints.Any(waypoint => waypoint.Id == item.OwnerId &&
                (active is null || waypoint.AssociatedTrackId == active.Id)) ||
            project.GuideNodes.Any(node => node.Id == item.OwnerId &&
                (active is null || node.TrackId == active.Id))))
            _dialogs.ShowWarning("图片格式提示", "KML格式不会携带项目中的图片文件。如需携带图片，请使用KMZ。");
        var path = _dialogs.ShowSaveDialog(format, active?.Name ?? project.Name);
        if (path is null) return;
        await RunTrackOperationAsync("正在导出轨迹……", async (progress, token) =>
        {
            if (format is TrackFileFormat.Kml or TrackFileFormat.Kmz)
            {
                if (active is not null)
                {
                    await new ProjectMergedExportService().ExportTrackAsync(project, active, null,
                        format, path, true, token);
                    StatusText = $"导出完成：{Path.GetFileName(path)}";
                    return;
                }
                var selection = new MergedExportSelection();
                foreach (var track in project.Tracks) selection.TrackIds.Add(track.Id);
                foreach (var waypoint in project.Waypoints) selection.MarkerIds.Add(waypoint.Id);
                foreach (var node in project.GuideNodes) selection.MarkerIds.Add(node.Id);
                await new ProjectMergedExportService().ExportAsync(project, selection, format, path, true, token);
                StatusText = $"导出完成：{Path.GetFileName(path)}";
                return;
            }
            await Task.Run(() => _trackFiles.ExportAsync(format, new TrackExportRequest
            {
                DocumentName = project.Name,
                Tracks = active is null ? project.Tracks.ToArray() : [active],
                Waypoints = active is null ? project.Waypoints.ToArray() :
                    project.Waypoints.Where(waypoint => waypoint.AssociatedTrackId == active.Id).ToArray()
            }, path, true, progress, token), token);
            StatusText = $"导出完成：{Path.GetFileName(path)}";
        });
    }

    private async Task MergedExportAsync()
    {
        var project = CurrentProject;
        if (project is null) return;
        var window = new MergedExportWindow(project) { Owner = System.Windows.Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        var format = window.Format;
        if (format != TrackFileFormat.Kmz && project.MediaAttachments.Any(item => window.Selection.MarkerIds.Contains(item.OwnerId)))
            _dialogs.ShowWarning("图片格式提示", "GPX/KML格式不会携带项目中的图片文件。如需携带图片，请使用KMZ。");
        var path = _dialogs.ShowSaveDialog(format, TrackExportNames.FileName(
            project.Tracks.Where(track => window.Selection.TrackIds.Contains(track.Id)).ToArray(), project.Name));
        if (path is null) return;
        await RunTrackOperationAsync("正在合并导出……", async (_, token) =>
        {
            await new ProjectMergedExportService().ExportAsync(project, window.Selection, format, path, true, token);
            StatusText = $"合并导出完成：{Path.GetFileName(path)}";
        });
    }

    private bool CanExportTreeTrack(object? item) => !IsBusy && CurrentProject is { } project && item switch
    {
        Track track => project.Tracks.Contains(track),
        SplitSection section => project.Tracks.Any(track => track.Id == section.TrackId &&
            track.GetSplitSections().Any(part => part.Id == section.Id)),
        _ => false
    };

    private async Task ExportTrackAsync(object? item, TrackFileFormat format)
    {
        var project = CurrentProject;
        if (project is null || item is null) return;
        var section = item as SplitSection;
        var track = item as Track ?? project.Tracks.FirstOrDefault(candidate => candidate.Id == section?.TrackId);
        if (track is null || !CanExportTreeTrack(item)) return;
        var path = _dialogs.ShowSaveDialog(format, section?.Name ?? track.Name ?? "导出轨迹");
        if (path is null) return;
        await RunTrackOperationAsync("正在导出轨迹……", async (progress, token) =>
        {
            if (format is TrackFileFormat.Kml or TrackFileFormat.Kmz)
            {
                await new ProjectMergedExportService().ExportTrackAsync(project, track, section,
                    format, path, true, token);
                StatusText = $"导出完成：{Path.GetFileName(path)}";
                return;
            }
            await Task.Run(() => _trackFiles.ExportAsync(format, new TrackExportRequest
            {
                DocumentName = track.Name ?? project.Name,
                Tracks = [track],
                Waypoints = project.Waypoints.Where(waypoint => waypoint.AssociatedTrackId == track.Id).ToArray()
            }, path, true, progress, token), token);
            StatusText = $"导出完成：{Path.GetFileName(path)}";
        });
    }

    private void SetActiveTrack(Track? track)
    {
        if (track is null || CurrentProject is null) return;
        var project = CurrentProject;
        if (project.ActiveTrackId == track.Id) return;
        project.SetActiveTrack(track.Id);
        _projects.OpenProjects.First(c => ReferenceEquals(c.Project, project)).SelectedTrackId = track.Id;
        RefreshProjectView();
        if (_trackEditState.IsActive && !ReferenceEquals(_trackEditTrack, track))
            _trackEditContextChangeTask = ActivateTrackEditTargetAsync(project, track);
        StatusText = $"当前工作轨迹：{track.Name ?? "未命名轨迹"}";
    }

    private async Task ChangeMainTrackAsync(Track? track)
    {
        var project = CurrentProject;
        if (project is null || project.MainGuideTrackId == track?.Id) return;
        if (!AutoConfirmTrackEditForAutomation)
        {
            var message = track is null
                ? "将主攻略轨迹转为普通轨迹，并将其所属攻略节点同步转为该轨迹下的普通标注。图片、原始坐标与人工内容保留，攻略分段归档，可一次撤销。之后可以合并、连接或拆分。继续吗？"
                : $"将“{track.Name ?? "（未命名轨迹）"}”设为主攻略轨迹，并将其所属普通标注全部转为攻略节点、吸附排序。若已有另一条主攻略轨迹，它及其节点先同步转为普通轨迹与普通标注。图片和人工内容保留，可撤销。继续吗？";
            if (MessageBox.Show(System.Windows.Application.Current.MainWindow, message,
                "确认更换主攻略轨迹", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        await RunProjectOperationAsync("正在重新计算攻略节点……", async (_, token) =>
        {
            var change = await Task.Run(() => MainGuideTrackChange.Prepare(project, track?.Id, token), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(CurrentProject, project)) return;
            var farCount = change.FarCount(project.Settings.GuideSnap.AutoSnapWarningDistanceMeters);
            if (farCount > 0 && !AutoConfirmTrackEditForAutomation && MessageBox.Show(System.Windows.Application.Current.MainWindow,
                    $"有 {farCount} 个标注距主攻略轨迹超过提醒距离，仍要同步转为攻略节点吗？", "确认较远攻略节点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _history.Execute("更换主攻略轨迹", change.Execute, change.Undo);
            RefreshProjectView();
            StatusText = track is null ? $"已转为普通轨迹；{change.ConvertedToWaypointCount} 个攻略节点同步转为普通标注，可撤销。" : $"主攻略轨迹：{track.Name ?? "（未命名轨迹）"}；{change.ConvertedToGuideCount} 个所属标注同步转为攻略节点，可撤销。";
        });
    }

    private void DeleteTrack(Track? track)
    {
        var project = CurrentProject;
        if (project is null || track is null) return;
        var transaction = new TrackManagementService().PrepareDeletion(project, track.Id);
        var impact = transaction.Impact;
        var detail = impact.IsMainGuideTrack
            ? $"项目将暂时没有主攻略轨迹；{impact.GuideNodesAttachedToMain} 个攻略节点会保留为未关联状态，{impact.GuideSegments} 个正式分段失效。不会自动选择其它轨迹。"
            : $"{impact.DirectWaypoints} 个普通标注会保留并移到“未关联标注”；{impact.SourceGuideNodes} 个来源于该轨迹的攻略节点继续保留在主攻略轨迹。";
        if (MessageBox.Show(System.Windows.Application.Current.MainWindow,
            $"确定从当前项目中删除轨迹“{track.Name ?? "（未命名轨迹）"}”吗？\n\n{detail}\n\n不会删除原始 GPX/KML/KMZ 文件。",
            "确认删除轨迹", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _history.Execute("删除轨迹", transaction.Execute, transaction.Undo);
        _guideStructure.ClearSnapCache();
        Map.InvalidateTrack(track.Id);
        RefreshProjectView();
        StatusText = $"已从项目删除轨迹：{track.Name ?? "（未命名轨迹）"}；可撤销。";
    }

    public void UpdateProjectItemSelection(NavigationNode node, ModifierKeys modifiers,
        bool preserveExisting = false, bool focusOnMap = true)
    {
        if (string.IsNullOrEmpty(node.SelectionKey)) return;
        var key = node.SelectionKey;
        if (preserveExisting && _selectedNavigationKeys.Contains(key)) return;
        _cancelledPrimaryKey = null;
        if (modifiers.HasFlag(ModifierKeys.Shift) && _selectionAnchorKey is not null)
        {
            var visible = VisibleSelectableNodes().ToArray();
            var start = Array.FindIndex(visible, item => item.SelectionKey == _selectionAnchorKey);
            var end = Array.FindIndex(visible, item => item.SelectionKey == key);
            if (start >= 0 && end >= 0)
            {
                if (!modifiers.HasFlag(ModifierKeys.Control)) _selectedNavigationKeys.Clear();
                for (var index = Math.Min(start, end); index <= Math.Max(start, end); index++)
                    _selectedNavigationKeys.Add(visible[index].SelectionKey);
            }
            else { _selectedNavigationKeys.Clear(); _selectedNavigationKeys.Add(key); }
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!_selectedNavigationKeys.Add(key)) _selectedNavigationKeys.Remove(key);
            _selectionAnchorKey = key;
        }
        else
        {
            _selectedNavigationKeys.Clear();
            _selectedNavigationKeys.Add(key);
            _selectionAnchorKey = key;
        }
        if (_selectedNavigationKeys.Contains(key))
        {
            if (node.Model is Track selectedTrack)
                foreach (var childSection in selectedTrack.GetSplitSections())
                    _selectedNavigationKeys.Remove($"拆分分段:{childSection.Id:D}");
            else if (node.Model is SplitSection selectedSection)
                _selectedNavigationKeys.Remove($"轨迹:{selectedSection.TrackId:D}");
        }
        SyncNavigationSelection();
        _primaryCandidateKeys.RemoveAll(k => !_selectedNavigationKeys.Contains(k));
        if (_selectedNavigationKeys.Contains(key)) { _primaryCandidateKeys.Remove(key); _primaryCandidateKeys.Add(key); SelectNavigationNode(node, false, true); }
        else
        {
            _cancelledPrimaryKey = key;
            var fallbackKey = _primaryCandidateKeys.LastOrDefault();
            var fallback = NavigationNodes.SelectMany(Flatten).LastOrDefault(n => n.SelectionKey == fallbackKey);
            SelectNavigationNode(fallback, false, true);
        }
        if (focusOnMap && node.Model is SplitSection section && !modifiers.HasFlag(ModifierKeys.Control) &&
            !modifiers.HasFlag(ModifierKeys.Shift) && !preserveExisting)
            _ = Map.FocusObjectAsync(section);
        static IEnumerable<NavigationNode> Flatten(NavigationNode n) { yield return n; foreach (var child in n.Children) foreach (var item in Flatten(child)) yield return item; }
    }

    private IEnumerable<NavigationNode> VisibleSelectableNodes()
    {
        foreach (var root in NavigationNodes)
            foreach (var node in Visit(root)) yield return node;

        static IEnumerable<NavigationNode> Visit(NavigationNode node)
        {
            yield return node;
            if (!node.IsExpanded) yield break;
            foreach (var child in node.Children)
                foreach (var descendant in Visit(child)) yield return descendant;
        }
    }

    private void SyncNavigationSelection()
    {
        var all = NavigationNodes.SelectMany(Visit).ToArray();
        _selectedNavigationKeys.IntersectWith(all.Select(node => node.SelectionKey));
        _selectedProjectItems.Clear();
        _selectedSplitSectionIds.Clear();
        var splitTrackIds = new HashSet<Guid>();
        foreach (var node in all.Where(node => _selectedNavigationKeys.Contains(node.SelectionKey)))
        {
            if (CurrentProject is not { } project) continue;
            switch (node.Model)
            {
                case Track track when project.Tracks.Contains(track): _selectedProjectItems.Add(track); break;
                case Waypoint point when project.Waypoints.Contains(point): _selectedProjectItems.Add(point); break;
                case GuideNode guide when project.GuideNodes.Contains(guide): _selectedProjectItems.Add(guide); break;
                case SplitSection split when project.Tracks.Any(track => track.Id == split.TrackId):
                    _selectedSplitSectionIds.Add(split.Id);
                    splitTrackIds.Add(split.TrackId);
                    break;
            }
        }
        _selectedSplitTrackId = splitTrackIds.Count == 1 ? splitTrackIds.Single() : null;
        UpdateSelectionHighlights();
        _ = Map.SetGeometrySelectionAsync(SelectedTracks().Select(track => track.Id), _selectedSplitSectionIds);
        RaiseCommandStates();
        AnalysisSelectionChanged?.Invoke(this, EventArgs.Empty);

        static IEnumerable<NavigationNode> Visit(NavigationNode node)
        {
            yield return node;
            foreach (var child in node.Children)
                foreach (var descendant in Visit(child)) yield return descendant;
        }
    }

    private void UpdateSelectionHighlights()
    {
        foreach (var root in NavigationNodes)
            Visit(root);
        void Visit(NavigationNode node)
        {
            node.IsMultiSelected = _selectedNavigationKeys.Contains(node.SelectionKey);
            foreach (var child in node.Children) Visit(child);
        }
        _showSelectedTracksCommand.RaiseCanExecuteChanged();
        _hideSelectedTracksCommand.RaiseCanExecuteChanged();
        _recolorSelectedTracksCommand.RaiseCanExecuteChanged();
        _deleteSelectedProjectItemsCommand.RaiseCanExecuteChanged();
        _associateSelectedWaypointsCommand.RaiseCanExecuteChanged();
        _convertSelectedWaypointsCommand.RaiseCanExecuteChanged();
        _convertSelectedGuideNodesCommand.RaiseCanExecuteChanged();
        ((RelayCommand)BatchEditMarkersCommand).RaiseCanExecuteChanged();
        ((RelayCommand<MapMarkerBase>)ChooseSnapBranchCommand).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedProjectItemCount));
    }

    private Track[] SelectedTracks() => CurrentProject?.Tracks.Where(_selectedProjectItems.Contains).ToArray() ?? [];
    private bool HasSelectedTracks() => !IsBusy && SelectedTracks().Length > 0;

    private void SetSelectedTrackVisibility(bool visible)
    {
        var project = CurrentProject;
        if (project is null) return;
        var tracks = SelectedTracks();
        if (tracks.Length == 0) return;
        var previous = tracks.ToDictionary(t => t.Id, t => MapLayerVisibility.IsTrackVisible(project.Settings.Map, t.Id));
        _history.Execute(visible ? "批量显示轨迹" : "批量隐藏轨迹",
            () => Apply(tracks.ToDictionary(t => t.Id, _ => visible)), () => Apply(previous));
        RefreshProjectView(false);
        _ = Map.RefreshVisibilityAsync();

        void Apply(IReadOnlyDictionary<Guid, bool> states) => project.ExecuteAtomicUpdate(p =>
        {
            foreach (var state in states) MapLayerVisibility.SetTrackVisible(p.Settings.Map, state.Key, state.Value);
            p.MarkDirty();
        });
    }

    private void SetTrackColor(Track? track)
    {
        var project = CurrentProject;
        if (project is null || track is null || !project.Tracks.Contains(track)) return;
        var window = new TrackColorWindow(track.DisplayColor ?? "#2877C7")
            { Owner = System.Windows.Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        var color = window.SelectedColor;
        if (project.Tracks.Any(item => item.Id != track.Id &&
            string.Equals(item.DisplayColor ?? "#2877C7", color, StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.ShowWarning("颜色已被使用", "项目内另一条轨迹已使用该颜色，请选择不同颜色。");
            return;
        }
        var old = track.DisplayColor;
        _history.Execute("修改轨迹颜色", () => Apply(color), () => Apply(old));
        RefreshProjectView();
        void Apply(string? value)
        {
            track.DisplayColor = value;
            project.MarkDirty();
            TrackColorChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EditProjectGeometryItem(object? model)
    {
        var project = CurrentProject;
        if (project is null || IsBusy) return;
        ProjectItemEditorWindow? window = model switch
        {
            Track track when project.Tracks.Contains(track) => new ProjectItemEditorWindow(track),
            SplitSection section when project.Tracks.Any(track => track.Id == section.TrackId &&
                track.GetSplitSections().Any(item => item.Id == section.Id)) => new ProjectItemEditorWindow(section),
            GuideSegment segment when project.GuideSegments.Contains(segment) =>
                new ProjectItemEditorWindow(segment, GuideSegmentDisplayFormatter.DefaultName(project, segment)),
            _ => null
        };
        if (window is null) return;
        window.Owner = System.Windows.Application.Current.MainWindow;
        if (window.ShowDialog() != true) return;
        if (model is Track targetTrack)
        {
            if (project.Tracks.Any(item => item.Id != targetTrack.Id &&
                string.Equals(item.DisplayColor ?? "#2877C7", window.EditedColor, StringComparison.OrdinalIgnoreCase)))
            {
                _dialogs.ShowWarning("颜色已被使用", "项目内另一条轨迹已使用该颜色，请选择不同颜色。");
                return;
            }
            var old = (targetTrack.Name, targetTrack.Description, targetTrack.DisplayColor, HikingGuide.Core.Services.TrackKilometerService.IsEnabled(targetTrack),targetTrack.Metadata.GetValueOrDefault(HikingGuide.Core.Services.KilometerRouteDirections.Key),targetTrack.Metadata.GetValueOrDefault(HikingGuide.Core.Services.TrackKilometerService.RouteKindKey));
            var next = (window.EditedName, window.EditedDescription, (string?)window.EditedColor, window.EditedKilometers??HikingGuide.Core.Services.TrackKilometerService.IsEnabled(targetTrack),window.EditedKilometerDirections??targetTrack.Metadata.GetValueOrDefault(HikingGuide.Core.Services.KilometerRouteDirections.Key),window.EditedKilometerKind??targetTrack.Metadata.GetValueOrDefault(HikingGuide.Core.Services.TrackKilometerService.RouteKindKey));
            if (old == next) return;
            _history.Execute("编辑轨迹属性", () => Apply(next), () => Apply(old));
            void Apply((string? Name, string? Description, string? Color, bool Kilometers,string? Directions,string? Kind) state) => project.ExecuteAtomicUpdate(p =>
            {
                targetTrack.Name = state.Name;
                targetTrack.Description = state.Description;
                targetTrack.DisplayColor = state.Color;
                HikingGuide.Core.Services.TrackKilometerService.SetEnabled(targetTrack,state.Kilometers);
                if(state.Kind is {} kind)targetTrack.Metadata[HikingGuide.Core.Services.TrackKilometerService.RouteKindKey]=kind;else targetTrack.Metadata.Remove(HikingGuide.Core.Services.TrackKilometerService.RouteKindKey);
                if(state.Directions is {} direction)targetTrack.Metadata[HikingGuide.Core.Services.KilometerRouteDirections.Key]=direction;else targetTrack.Metadata.Remove(HikingGuide.Core.Services.KilometerRouteDirections.Key);
                p.MarkDirty();
            });
            RefreshProjectView();
            if (_mapNodes.TryGetValue(targetTrack.Id, out var selected)) SelectNavigationNode(selected, false);
        }
        else if (model is SplitSection chosenSection &&
            project.Tracks.FirstOrDefault(track => track.Id == chosenSection.TrackId) is { } owner)
        {
            var section = owner.GetSplitSections().FirstOrDefault(item => item.Id == chosenSection.Id);
            if (section is null) return;
            var old = section.GeometryParts.Select(part =>
                (Part: part, Name: part.SplitSectionName, Description: part.SplitSectionDescription)).ToArray();
            if (old.All(item => item.Name == window.EditedName &&
                item.Description == window.EditedDescription)) return;
            _history.Execute("编辑拆分分段属性", () => Apply(window.EditedName, window.EditedDescription),
                () => project.ExecuteAtomicUpdate(p =>
                {
                    foreach (var item in old)
                    {
                        item.Part.SplitSectionName = item.Name;
                        item.Part.SplitSectionDescription = item.Description;
                    }
                    p.MarkDirty();
                }));
            void Apply(string? name, string? description) => project.ExecuteAtomicUpdate(p =>
            {
                foreach (var part in section.GeometryParts)
                {
                    part.SplitSectionName = name;
                    part.SplitSectionDescription = description;
                }
                p.MarkDirty();
            });
            RefreshProjectView();
            if (_mapNodes.TryGetValue(section.Id, out var selected)) SelectNavigationNode(selected, false);
        }
        else if (model is GuideSegment guideSegment && project.GuideSegments.Contains(guideSegment))
        {
            var oldName = guideSegment.CustomName;
            var oldManual = guideSegment.ManualData?.DeepCopy();
            SegmentManualData? nextManual = window.EditedDescription is null &&
                window.EditedRoadCondition is null && window.EditedDuration is null ? null : new SegmentManualData
            {
                Description = window.EditedDescription,
                RoadCondition = window.EditedRoadCondition,
                PlannedDuration = window.EditedDuration
            };
            if (oldName == window.EditedName && oldManual?.Description == nextManual?.Description &&
                oldManual?.RoadCondition == nextManual?.RoadCondition &&
                oldManual?.PlannedDuration == nextManual?.PlannedDuration) return;
            _history.Execute("编辑攻略分段属性", () => Apply(window.EditedName, nextManual),
                () => Apply(oldName, oldManual));
            void Apply(string? name, SegmentManualData? manual) => project.ExecuteAtomicUpdate(p =>
            {
                guideSegment.CustomName = name;
                guideSegment.ManualData = manual?.DeepCopy();
                p.MarkDirty();
            });
            RefreshProjectView();
            if (_mapNodes.TryGetValue(guideSegment.Id, out var selected)) SelectNavigationNode(selected, false);
        }
        StatusText = "项目内容属性已修改，可撤销。";
    }

    private void RecolorSelectedTracks()
    {
        var project = CurrentProject;
        if (project is null) return;
        var selected = SelectedTracks();
        if (selected.Length == 0) return;
        var previous = selected.ToDictionary(track => track.Id, track => track.DisplayColor);
        var used = project.Tracks.Except(selected).ToList();
        var assigned = new Dictionary<Guid, string?>();
        foreach (var track in selected)
        {
            var color = TrackDisplayColor.NextUnique(used);
            assigned[track.Id] = color;
            used.Add(new Track { DisplayColor = color });
        }
        _history.Execute("批量分配轨迹颜色", () => Apply(assigned), () => Apply(previous));
        RefreshProjectView();
        void Apply(IReadOnlyDictionary<Guid, string?> colors)
        {
            foreach (var track in selected) track.DisplayColor = colors[track.Id];
            project.MarkDirty();
            TrackColorChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DeleteSelectedProjectItems()
    {
        var project = CurrentProject;
        if (project is null) return;
        var tracks = SelectedTracks();
        var waypoints = project.Waypoints.Where(_selectedProjectItems.Contains).ToArray();
        var nodes = project.GuideNodes.Where(_selectedProjectItems.Contains).ToArray();
        if (tracks.Length + waypoints.Length + nodes.Length == 0) return;
        var message = $"确定从项目中删除所选的 {tracks.Length} 条轨迹、{waypoints.Length} 个普通标注和 {nodes.Length} 个攻略节点吗？\n\n" +
            "删除轨迹后，其未选中的标注仍会保留在“未关联标注”；原始 GPX/KML/KMZ 文件不会删除。此操作可撤销。";
        if (MessageBox.Show(System.Windows.Application.Current.MainWindow, message,
            "确认批量删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var ownerIds = waypoints.Select(item => item.Id).Concat(nodes.Select(item => item.Id)).ToHashSet();
        var attachments = project.MediaAttachments.Where(item => ownerIds.Contains(item.OwnerId)).ToArray();
        var external = project.ExternalMediaReferences.Where(item => ownerIds.Contains(item.OwnerId)).ToArray();
        var blobs = project.MediaBlobs.Where(blob => attachments.Any(item => item.MediaBlobId == blob.Id) &&
            !project.MediaAttachments.Any(item => item.MediaBlobId == blob.Id && !ownerIds.Contains(item.OwnerId))).ToArray();
        var oldSegments = project.GuideSegments.ToArray();
        var oldRetired = project.RetiredGuideSegments.ToArray();
        List<TrackDeletionTransaction> transactions = [];
        _history.Execute("批量删除轨迹与标注", () =>
        {
            project.ExecuteAtomicUpdate(p =>
            {
                foreach (var item in waypoints) p.Waypoints.Remove(item);
                foreach (var item in nodes) p.GuideNodes.Remove(item);
                foreach (var item in attachments) p.MediaAttachments.Remove(item);
                foreach (var item in external) p.ExternalMediaReferences.Remove(item);
                foreach (var item in blobs) p.MediaBlobs.Remove(item);
                if (nodes.Length > 0) GuideStructureService.ReplaceSegments(p, _guideStructure.RebuildSegments(p));
            });
            transactions = [];
            foreach (var track in tracks)
            {
                var transaction = new TrackManagementService().PrepareDeletion(project, track.Id);
                transaction.Execute();
                transactions.Add(transaction);
                Map.InvalidateTrack(track.Id);
            }
        }, () =>
        {
            for (var index = transactions.Count - 1; index >= 0; index--) transactions[index].Undo();
            project.ExecuteAtomicUpdate(p =>
            {
                foreach (var item in waypoints) p.Waypoints.Add(item);
                foreach (var item in nodes) p.GuideNodes.Add(item);
                foreach (var item in blobs) p.MediaBlobs.Add(item);
                foreach (var item in attachments) p.MediaAttachments.Add(item);
                foreach (var item in external) p.ExternalMediaReferences.Add(item);
                GuideStructureService.ReplaceSegments(p, oldSegments);
                p.RetiredGuideSegments.Clear();
                foreach (var item in oldRetired) p.RetiredGuideSegments.Add(item);
            });
        });
        _selectedProjectItems.Clear();
        _selectedNavigationKeys.Clear();
        RefreshProjectView();
        StatusText = "已批量删除所选项目内容；可一次撤销。";
    }

    private void AssociateSelectedWaypoints()
    {
        var project = CurrentProject;
        var track = project?.ActiveTrack;
        if (project is null || track is null) return;
        var points = project.Waypoints.Where(_selectedProjectItems.Contains).ToArray();
        if (points.Length == 0) return;
        var previous = points.ToDictionary(point => point.Id, point => point.AssociatedTrackId);
        _history.Execute("批量关联普通标注", () => Apply(track.Id), () =>
        {
            project.ExecuteAtomicUpdate(p =>
            {
                foreach (var point in points) point.AssociatedTrackId = previous[point.Id];
                p.MarkDirty();
            });
        });
        RefreshProjectView();
        StatusText = $"已将 {points.Length} 个普通标注关联到“{track.Name ?? "未命名轨迹"}”；可撤销。";
        void Apply(Guid trackId) => project.ExecuteAtomicUpdate(p =>
        {
            foreach (var point in points) point.AssociatedTrackId = trackId;
            p.MarkDirty();
        });
    }

    private async Task ConvertSelectedWaypointsAsync()
    {
        var project = CurrentProject;
        if (project?.MainGuideTrack is null) return;
        var selected = project.Waypoints.Where(_selectedProjectItems.Contains).ToArray();
        if (selected.Length == 0) return;
        await RunOperationAsync("正在后台吸附所选标注并计算攻略分段……", async token =>
        {
            var plan = await Task.Run(() => new BatchWaypointGuideConversion().Prepare(project, selected, token), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(CurrentProject, project)) return;
            var warningDistance = project.Settings.GuideSnap.AutoSnapWarningDistanceMeters;
            var farCount = plan.FarCount(warningDistance);
            if (farCount > 0 && MessageBox.Show(System.Windows.Application.Current.MainWindow,
                $"有 {farCount} 个所选标注距主攻略轨迹超过 {warningDistance:F0} 米。仍要转换为攻略节点吗？",
                "确认较远攻略节点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                StatusText = "已取消批量转换，项目未修改。";
                return;
            }
            _history.Execute("批量转为攻略节点", plan.Apply, plan.Undo);
            _selectedProjectItems.Clear();
            _selectedNavigationKeys.Clear();
            RefreshProjectView();
            StatusText = $"已将 {plan.Count} 个普通标注转为攻略节点；图片和人工内容已保留，可一次撤销。";
        });
    }

    private async Task ConvertSelectedGuideNodesAsync()
    {
        var project = CurrentProject;
        if (project is null) return;
        var selected = project.GuideNodes.Where(_selectedProjectItems.Contains).ToArray();
        if (selected.Length == 0) return;
        await RunOperationAsync("正在后台计算转换后的攻略分段……", async token =>
        {
            var plan = await Task.Run(() => new BatchGuideWaypointConversion().Prepare(project, selected, token), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(CurrentProject, project)) return;
            _history.Execute("批量转为普通节点", plan.Apply, plan.Undo);
            _selectedProjectItems.Clear();
            _selectedNavigationKeys.Clear();
            RefreshProjectView();
            StatusText = $"已将 {plan.Count} 个攻略节点转为普通标注；图片和人工内容已保留，可一次撤销。";
        });
    }

    public async Task ConvertMapMarkerAsync(MapMarkerBase marker, bool toGuide)
    {
        var project = CurrentProject;
        if (IsBusy || project is null) return;
        if (toGuide && marker is Waypoint waypoint && project.Waypoints.Contains(waypoint))
        {
            if (project.MainGuideTrack is null) return;
            await RunOperationAsync("正在后台转换为攻略节点……", async token =>
            {
                var plan = await Task.Run(() => new BatchWaypointGuideConversion()
                    .Prepare(project, [waypoint], token), token);
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(CurrentProject, project) || !project.Waypoints.Contains(waypoint)) return;
                var warning = project.Settings.GuideSnap.AutoSnapWarningDistanceMeters;
                if (plan.FarCount(warning) > 0 && MessageBox.Show(
                    System.Windows.Application.Current.MainWindow,
                    $"该标注距主攻略轨迹超过 {warning:F0} 米。仍要转换吗？",
                    "确认较远攻略节点", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                _history.Execute("普通标注转攻略节点", plan.Apply, plan.Undo);
                RefreshProjectView();
                SelectMarkerFromMapContext(plan.NewNodes.Single());
                StatusText = "已转为攻略节点，可撤销。";
            });
        }
        else if (!toGuide && marker is GuideNode guide && project.GuideNodes.Contains(guide))
        {
            await RunOperationAsync("正在后台转换为普通标注……", async token =>
            {
                var plan = await Task.Run(() => new BatchGuideWaypointConversion()
                    .Prepare(project, [guide], token), token);
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(CurrentProject, project) || !project.GuideNodes.Contains(guide)) return;
                _history.Execute("攻略节点转普通标注", plan.Apply, plan.Undo);
                RefreshProjectView();
                SelectMarkerFromMapContext(plan.NewWaypoints.Single());
                StatusText = "已转为普通标注点，可撤销。";
            });
        }
    }

    internal bool IsProjectTreeRefreshActive=>_isRefreshingTree||_suppressTreeSelection;
    private void RefreshProjectView(bool refreshMap = true)
    {
        if(_isRefreshingTree)return;
        _isRefreshingTree=true;
        try
        {
            if(_markerThumbnailProject!=CurrentProject?.Id)_markerThumbnailLifetime.Cancel();
            CheckRoutePlanProjectSwitch();
            // 撤销/重做或提交编辑重建树时，WPF 可能在稍后的 DataBind/Loaded 阶段恢复选中项。
            // 该事件不是用户主动定位；保护到本轮布局绑定完成，避免自动适应整条轨迹。
            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
            {
                _suppressTreeRebuildFocus = true;
                var focusVersion = ++_treeRebuildFocusVersion;
                dispatcher.BeginInvoke(() =>
                {
                    if (_treeRebuildFocusVersion == focusVersion) _suppressTreeRebuildFocus = false;
                }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            var expanded = CaptureExpandedNodes();
            var switched = !ReferenceEquals(_historyProject, CurrentProject);
            if (switched)
            {
                _cancelledPrimaryKey = null; _primaryCandidateKeys.Clear();
                _trackEditState.End();
                _trackEditContextChangeTask = Task.CompletedTask;
                _trackEditPick = null;
                _trackRangeStart = null;
                _trackRangeEnd = null;
                _trackClickMode = TrackClickMode.SelectPoint;
                _selectedProjectItems.Clear();
                _selectedNavigationKeys.Clear();
                _selectionAnchorKey = null;
                _historyProject = CurrentProject;
                if (CurrentProject is { } current)
                {
                    var contextId = _projects.OpenProjects.First(item => ReferenceEquals(item.Project, current)).ContextId;
                    if (!_histories.TryGetValue(contextId, out var history))
                    {
                        history = new ProjectEditHistory { Project = current };
                        history.Changed += (_, _) =>
                        {
                            ((RelayCommand)UndoCommand).RaiseCanExecuteChanged();
                            ((RelayCommand)RedoCommand).RaiseCanExecuteChanged();
                        };
                        _histories.Add(contextId, history);
                    }
                    _history = history;
                }
                _guideStructure.ClearSnapCache();
            }
            NavigationNodes.Clear();
            GuideRows.Clear();
            _selectedModel = null;
            _mapNodes.Clear();
            SelectedProperties.Clear();
            var project = CurrentProject;
            foreach (var context in _projects.OpenProjects)
            {
                var itemProject = context.Project;
                var activeProject = ReferenceEquals(itemProject, project);
                var root = new NavigationNode($"{(activeProject ? "● " : "")}{itemProject.Name}{(itemProject.IsDirty ? " *" : "")}",
                    itemProject, isActiveProject: activeProject);
                root.IsExpanded = activeProject;
                NavigationNodes.Add(root);
                if (!activeProject)
                {
                    foreach (var track in itemProject.Tracks)
                    {
                        var trackNode = new NavigationNode((track.Id == itemProject.MainGuideTrackId ? "★ " : "") +
                            (track.Name ?? "（未命名轨迹）"), track);
                        if (track.Id == itemProject.MainGuideTrackId)
                        {
                            var guides = itemProject.GuideNodes.Where(marker => marker.TrackId == track.Id && !marker.IsOrphaned)
                                .OrderBy(marker => marker.DistanceFromStart).ThenBy(marker => marker.StableOrder).ToArray();
                            var guideGroup = new NavigationNode($"攻略节点（{guides.Length}）");
                            for (var index = 0; index < guides.Length; index++)
                                guideGroup.Children.Add(new NavigationNode($"{index + 1} {guides[index].Name ?? "未命名攻略节点"}", guides[index]));
                            trackNode.Children.Add(guideGroup);
                            var segmentGroup = new NavigationNode($"攻略分段（{itemProject.GuideSegments.Count}）");
                            foreach (var segment in itemProject.GuideSegments.Where(segment => segment.TrackId == track.Id))
                                segmentGroup.Children.Add(new NavigationNode(GuideSegmentDisplayFormatter.Format(itemProject, segment), segment));
                            trackNode.Children.Add(segmentGroup);
                        }
                        var owned = itemProject.Waypoints.Where(marker => marker.AssociatedTrackId == track.Id).ToArray();
                        var waypointGroup = new NavigationNode($"普通标注（{owned.Length}）");
                        foreach (var marker in owned)
                            waypointGroup.Children.Add(new NavigationNode(marker.Name ?? "未命名标注", marker));
                        trackNode.Children.Add(waypointGroup);
                        var splitSections = track.GetSplitSections();
                        var geometryGroup = new NavigationNode($"拆分分段（{splitSections.Count}）");
                        foreach (var section in splitSections)
                        {
                            geometryGroup.Children.Add(new NavigationNode(section.Name, section));
                        }
                        trackNode.Children.Add(geometryGroup);
                        root.Children.Add(trackNode);
                    }
                    var otherOrphans = itemProject.GuideNodes.Where(marker => marker.IsOrphaned || itemProject.MainGuideTrackId is null).ToArray();
                    if (otherOrphans.Length > 0)
                    {
                        var group = new NavigationNode($"未关联攻略节点（{otherOrphans.Length}）");
                        foreach (var marker in otherOrphans) group.Children.Add(new NavigationNode(marker.Name ?? "未命名攻略节点", marker));
                        root.Children.Add(group);
                    }
                    var otherUnassociated = itemProject.Waypoints.Where(marker => marker.AssociatedTrackId is null).ToArray();
                    if (otherUnassociated.Length > 0)
                    {
                        var group = new NavigationNode($"未关联标注（{otherUnassociated.Length}）");
                        foreach (var marker in otherUnassociated) group.Children.Add(new NavigationNode(marker.Name ?? "未命名标注", marker));
                        root.Children.Add(group);
                    }
                    continue;
                }
                if (project is null) continue;
                foreach (var track in project.Tracks)
                {
                    var label = (track.Id == project.MainGuideTrackId ? "★ " : "") + (track.Name ?? "（未命名轨迹）");
                    var node = new NavigationNode(label, track,
                        MapLayerVisibility.IsTrackVisible(project.Settings.Map, track.Id),
                        visible => SetMapLayerVisibility(track.Id, false, visible));
                    _mapNodes[track.Id] = node;
                    if (track.Id == project.MainGuideTrackId)
                    {
                        var guides = project.GuideNodes.Where(marker => marker.TrackId == track.Id && !marker.IsOrphaned)
                            .OrderBy(marker => marker.DistanceFromStart).ThenBy(marker => marker.StableOrder).ToArray();
                        var guideGroup = new NavigationNode($"攻略节点（{guides.Length}）");
                        for (var index = 0; index < guides.Length; index++)
                        {
                            var marker = guides[index];
                            var markerNode = new NavigationNode($"{index + 1} {marker.Name ?? "未命名攻略节点"}", marker);
                            _mapNodes[marker.Id] = markerNode;
                            guideGroup.Children.Add(markerNode);
                            var incoming = index == 0 ? null : project.GuideSegments.FirstOrDefault(segment =>
                                segment.StartNodeId == guides[index - 1].Id && segment.EndNodeId == marker.Id);
                            GuideRows.Add(new GuideTableRow(index + 1, marker, incoming,
                                project.Settings.GuideSnap.AutoSnapWarningDistanceMeters));
                        }
                        node.Children.Add(guideGroup);
                        var segments = new NavigationNode($"攻略分段（{project.GuideSegments.Count}）");
                        foreach (var segment in project.GuideSegments.Where(segment => segment.TrackId == track.Id))
                        {
                            var segmentNode = new NavigationNode(GuideSegmentDisplayFormatter.Format(project, segment), segment,
                                MapLayerVisibility.IsGuideSegmentVisible(project.Settings.Map, segment.Id),
                                visible => SetMapLayerVisibility(segment.Id, true, visible));
                            _mapNodes[segment.Id] = segmentNode;
                            segments.Children.Add(segmentNode);
                        }
                        node.Children.Add(segments);
                    }
                    var owned = project.Waypoints.Where(marker => marker.AssociatedTrackId == track.Id).ToArray();
                    var waypointGroup = new NavigationNode($"普通标注（{owned.Length}）");
                    foreach (var marker in owned)
                    {
                        var markerNode = new NavigationNode(marker.Name ?? "未命名标注", marker);
                        _mapNodes[marker.Id] = markerNode;
                        waypointGroup.Children.Add(markerNode);
                    }
                    node.Children.Add(waypointGroup);
                    var splitSections = track.GetSplitSections();
                    var geometryGroup = new NavigationNode($"拆分分段（{splitSections.Count}）");
                    foreach (var section in splitSections)
                    {
                        var segmentNode = new NavigationNode(section.Name, section);
                        _mapNodes[section.Id] = segmentNode;
                        geometryGroup.Children.Add(segmentNode);
                    }
                    node.Children.Add(geometryGroup);
                    root.Children.Add(node);
                }
                var orphaned = project.GuideNodes.Where(marker => marker.IsOrphaned || project.MainGuideTrackId is null).ToArray();
                if (orphaned.Length > 0)
                {
                    var group = new NavigationNode($"未关联攻略节点（{orphaned.Length}）");
                    foreach (var marker in orphaned)
                    {
                        var nav = new NavigationNode(marker.Name ?? "未命名攻略节点", marker);
                        _mapNodes[marker.Id] = nav;
                        group.Children.Add(nav);
                    }
                    root.Children.Add(group);
                }
                var unassociated = project.Waypoints.Where(marker => marker.AssociatedTrackId is null).ToArray();
                if (unassociated.Length > 0)
                {
                    var group = new NavigationNode($"未关联标注（{unassociated.Length}）");
                    foreach (var marker in unassociated)
                    {
                        var nav = new NavigationNode(marker.Name ?? "未命名标注", marker);
                        _mapNodes[marker.Id] = nav;
                        group.Children.Add(nav);
                    }
                    root.Children.Add(group);
                }
                var points = project.Tracks.Sum(track => track.GetPointCount());
                StatusText = $"项目：{project.Name} | 轨迹：{project.Tracks.Count}条 | 轨迹点：{points} | 标注点：{project.Waypoints.Count}";
            }
            RestoreExpandedNodes(expanded);
            SyncNavigationSelection();
            if (switched)
                foreach (var root in NavigationNodes.Where(node => ReferenceEquals(node.Model, project)))
                    root.IsExpanded = true;
            if (project is not null)
            {
                var context = _projects.OpenProjects.First(c => ReferenceEquals(c.Project, project));
                if (context.SelectedTrackId.HasValue && !project.Tracks.Any(t => t.Id == context.SelectedTrackId)) context.SelectedTrackId = null;
                project.ActiveTrackId = context.SelectedTrackId;
                if (context.SelectedObjectId is Guid objectId &&
                    (objectId==project.Id?NavigationNodes.FirstOrDefault(n=>ReferenceEquals(n.Model,project)):_mapNodes.GetValueOrDefault(objectId)) is {} selectedNode)
                {
                    _selectedModel = selectedNode.Model; _selectedNavigationKeys.Add(selectedNode.SelectionKey);
                    _suppressTreeSelection = true; try { selectedNode.IsSelected = true; } finally { _suppressTreeSelection = false; }
                    SyncNavigationSelection();
                }
            }
            NotifyPrimaryTrack();
            AnalysisSelectionChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(ProjectName));
            RaiseCommandStates();
            if (refreshMap)
            {
                // 删除当前目标后仍在项目编辑模式中，改选剩余轨迹；空项目才退出。
                if (project is not null && ReferenceEquals(project, _trackEditProject) &&
                    (_trackEditTrack is null || !project.Tracks.Contains(_trackEditTrack)))
                {
                    if ((project.ActiveTrack ?? project.Tracks.FirstOrDefault()) is { } remaining)
                        _trackEditState.SelectTarget(project, remaining, renewSession: true);
                    else _trackEditState.End();
                }
                _mapRefreshTask = Map.SetProjectAsync(project);
                if (project is not null && ReferenceEquals(project, _trackEditProject) &&
                    _trackEditTrack is { } editing)
                {
                    // 撤销/重做可将 GeometryRevision 回退，故重建时也必须轮换会话。
                    _trackEditState.SelectTarget(project, editing, renewSession: true);
                    _trackEditPick = null;
                    _trackRangeStart = null;
                    _trackRangeEnd = null;
                    _trackClickMode = TrackClickMode.SelectPoint;
                    var session = _trackEditSessionId!.Value;
                    _mapRefreshTask = RestoreTrackEditAfterMapRefreshAsync(_mapRefreshTask,
                        project, editing, session);
                    _trackEditContextChangeTask = _mapRefreshTask;
                }
                OnPropertyChanged(nameof(IsTrackEditActive));
                OnPropertyChanged(nameof(SelectedTrackEditPointId));
                RaiseCommandStates();
            }
        }
        finally { _isRefreshingTree=false; }
    }

    private async Task RestoreTrackEditAfterMapRefreshAsync(Task refresh,
        HikingProject project, Track track, Guid session)
    {
        await refresh;
        if (ReferenceEquals(CurrentProject, project) && _trackEditSessionId == session &&
            ReferenceEquals(_trackEditTrack, track))
            await Map.BeginTrackEditAsync(track, session);
    }

    private HashSet<string> CaptureExpandedNodes()
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in NavigationNodes)
            Capture(root, RootKey(root));
        return expanded;

        void Capture(NavigationNode node, string key)
        {
            if (node.IsExpanded) expanded.Add(key);
            for (var index = 0; index < node.Children.Count; index++)
                Capture(node.Children[index], ChildKey(key, node.Children[index], index));
        }
    }

    private string RootKey(NavigationNode root)
    {
        var context = _projects.OpenProjects.FirstOrDefault(item => ReferenceEquals(item.Project, root.Model));
        return context is null ? "项目" : $"项目:{context.ContextId:D}";
    }

    private void RestoreExpandedNodes(HashSet<string> expanded)
    {
        foreach (var root in NavigationNodes)
            Restore(root, RootKey(root));

        void Restore(NavigationNode node, string key)
        {
            node.SelectionKey = key;
            node.IsExpanded = expanded.Count == 0 ? node.IsExpanded : expanded.Contains(key);
            for (var index = 0; index < node.Children.Count; index++)
                Restore(node.Children[index], ChildKey(key, node.Children[index], index));
        }
    }

    private static string ChildKey(string parent, NavigationNode node, int index) => node.Model switch
    {
        Track track => $"轨迹:{track.Id:D}",
        Waypoint waypoint => $"标注:{waypoint.Id:D}",
        GuideNode guide => $"攻略:{guide.Id:D}",
        GuideSegment segment => $"分段:{segment.Id:D}",
        TrackSegment trackSegment => $"轨迹段:{trackSegment.Id:D}",
        SplitSection section => $"拆分分段:{section.Id:D}",
        _ => $"{parent}/组:{node.DisplayName.Split('（')[0]}"
    };

    private void OnMapSelectionRequested(object? sender, HikingGuide.Application.Maps.MapSelection selection)
    {
        if (!selection.ObjectId.HasValue || !_mapNodes.TryGetValue(selection.ObjectId.Value, out var node)) return;
        MapSelectionSyncCount++;
        _isMapSelectionInProgress = true;
        try
        {
            _cancelledPrimaryKey = null;
            UpdateProjectItemSelection(node, selection.CtrlKey ? ModifierKeys.Control : ModifierKeys.None, focusOnMap: false);
        }
        finally { _isMapSelectionInProgress = false; }
    }

    private void OnMapSplitSectionSelectionRequested(object? sender, MapEventMessage value)
    {
        if (CurrentProject?.Tracks.FirstOrDefault(track => track.Id == value.TrackId) is not { } track ||
            value.SplitSectionId is not Guid id) return;
        var section = track.GetSplitSections().FirstOrDefault(item => item.Id == id);
        if (section is null) return;
        if (_mapNodes.TryGetValue(id, out var node))
        {
            var previousSuppression = _suppressTrackEditTargetSynchronization;
            if (value.Type == "SplitSectionContextMenuRequested")
                _suppressTrackEditTargetSynchronization = true;
            _isMapSelectionInProgress = true;
            try
            {
                UpdateProjectItemSelection(node, value.CtrlKey ? ModifierKeys.Control : ModifierKeys.None,
                    value.Type == "SplitSectionContextMenuRequested", focusOnMap: false);
            }
            finally
            {
                _isMapSelectionInProgress = false;
                _suppressTrackEditTargetSynchronization = previousSuppression;
            }
        }
    }

    private void ClearSplitSectionSelection()
    {
        if (_selectedSplitSectionIds.Count == 0) return;
        _selectedNavigationKeys.RemoveWhere(key => key.StartsWith("拆分分段:", StringComparison.Ordinal));
        _selectedSplitSectionIds.Clear();
        _selectedSplitTrackId = null;
        _ = Map.SetSplitSectionSelectionAsync([]);
        UpdateSelectionHighlights();
    }

    public void SelectMarkerFromMapContext(MapMarkerBase marker)
    {
        if (CurrentProject is not { } project ||
            marker is Waypoint point && !project.Waypoints.Contains(point) ||
            marker is GuideNode guide && !project.GuideNodes.Contains(guide) ||
            !_mapNodes.TryGetValue(marker.Id, out var node)) return;
        node.IsSelected = true;
        SelectNavigationNode(node, false);
    }

    public void SelectTrackFromMapContext(Track track)
    {
        if (CurrentProject?.Tracks.Contains(track) != true ||
            !_mapNodes.TryGetValue(track.Id, out var node)) return;
        _isMapSelectionInProgress = true;
        try { UpdateProjectItemSelection(node, ModifierKeys.None, preserveExisting: true, focusOnMap: false); }
        finally { _isMapSelectionInProgress = false; }
    }

    private void OnMapMediaOpenRequested(object? sender, HikingGuide.Application.Maps.MapEventMessage value)
    {
        var id = value.WaypointId ?? value.GuideNodeId;
        if (id is not Guid markerId || !_mapNodes.TryGetValue(markerId, out var node)) return;
        SelectNavigationNode(node, false);
        OpenSelectedMedia();
    }

    private void OnMapMediaThumbnailRequested(object? sender, HikingGuide.Application.Maps.MapEventMessage value) =>
        _ = PrepareMapThumbnailAsync(value);

    private Task PrepareMapThumbnailAsync(HikingGuide.Application.Maps.MapEventMessage value)=>PrepareMarkerThumbnailAsync(value);

    private void SetMapLayerVisibility(Guid objectId, bool guideSegment, bool visible)
    {
        var project = CurrentProject;
        if (project is null) return;
        project.ExecuteAtomicUpdate(p =>
        {
            if (guideSegment) MapLayerVisibility.SetGuideSegmentVisible(p.Settings.Map, objectId, visible);
            else MapLayerVisibility.SetTrackVisible(p.Settings.Map, objectId, visible);
            p.MarkDirty();
        });
        OnPropertyChanged(nameof(WindowTitle));
        _ = Map.RefreshVisibilityAsync();
    }

    private async Task BeginGuidePositionPickAsync(GuideNode? node)
    {
        if (node is null) return;
        await Map.BeginSnapLocationPickAsync(node.Id);
        StatusText = $"请在地图上点击“{node.Name ?? "攻略节点"}”应关联的轨迹线段。";
    }

    private async Task BeginMarkerAddAsync(bool guideNode)
    {
        await Map.BeginMarkerAddAsync(guideNode);
        StatusText = guideNode ? "请在地图上点击要添加攻略节点的位置；按 Esc 取消。" :
            "请在地图上点击要添加普通标注的位置；按 Esc 取消。";
    }

    private async Task CheckGuidesAsync()
    {
        var project = CurrentProject;
        if (project is null) return;
        await RunOperationAsync("正在后台检查攻略节点……", async token =>
        {
            var result = await Task.Run(() => new ProjectValidator().Validate(project), token);
            var issues = result.Errors.Concat(result.Warnings).Take(20).ToArray();
            var summary = $"错误 {result.Errors.Count} 项，提醒 {result.Warnings.Count} 项。";
            _dialogs.ShowInformation("攻略节点检查", issues.Length == 0 ? summary + "\n未发现问题。" :
                summary + "\n" + string.Join("\n", issues) + (result.Errors.Count + result.Warnings.Count > 20 ? "\n仅显示前 20 项。" : ""));
            StatusText = "攻略节点检查完成：" + summary;
        });
    }

    private async Task ResnapSelectedGuideAsync()
    {
        var project = CurrentProject;
        var node = SelectedMarker as GuideNode;
        var track = project?.Tracks.FirstOrDefault(item => item.Id == node?.TrackId);
        if (project is null || node is null || track is null) return;
        await RunOperationAsync("正在后台重新吸附攻略节点……", async token =>
        {
            var position = await Task.Run(() => _guideStructure.SnapNodeToPosition(track, node.OriginalCoordinate, node.PreferredTrackPosition), token);
            if (position.DistanceToTrack > project.Settings.GuideSnap.AutoSnapWarningDistanceMeters &&
                MessageBox.Show(System.Windows.Application.Current.MainWindow,
                    $"该节点距轨迹 {position.DistanceToTrack:F1} 米，仍要重新吸附吗？", "确认较远攻略节点",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var previous = node.ExactPosition;
            var previousOrphaned = node.IsOrphaned;
            var oldSegments = project.GuideSegments.ToArray();
            GuideSegment[]? newSegments = null;
            _history.Execute("重新吸附攻略节点", () => project.ExecuteAtomicUpdate(p =>
            {
                _guideStructure.ApplyPosition(node, position);
                newSegments ??= _guideStructure.RebuildSegments(p).ToArray();
                ReplaceGuideSegments(p, newSegments);
                p.NotifyGuideNodeChanged();
            }), () => project.ExecuteAtomicUpdate(p =>
            {
                if (previous is not null) _guideStructure.ApplyPosition(node, previous);
                else { node.ExactPosition = null; node.SnappedCoordinate = null; node.DistanceFromStart = null; node.DistanceToTrack = null; }
                node.IsOrphaned = previousOrphaned;
                ReplaceGuideSegments(p, oldSegments);
                p.NotifyGuideNodeChanged();
            }));
            RefreshProjectView();
            StatusText = $"已重新吸附攻略节点：{node.Name ?? "未命名节点"}";
        });
    }

    public async Task SnapMapMarkerAsync(MapMarkerBase marker, bool nearestAnyTrack, bool chooseBranch=false)
    {
        var project = CurrentProject;
        if (IsBusy || project is null ||
            marker is Waypoint point && !project.Waypoints.Contains(point) ||
            marker is GuideNode guide && !project.GuideNodes.Contains(guide) ||
            nearestAnyTrack && marker is not Waypoint) return;
        await RunOperationAsync("正在完整轨迹上寻找最近吸附位置……", async token =>
        {
            var current = marker switch
            {
                Waypoint sourceWaypoint => sourceWaypoint.Coordinate,
                GuideNode sourceGuide => sourceGuide.SnappedCoordinate ?? sourceGuide.OriginalCoordinate,
                _ => throw new InvalidOperationException("该标注不支持轨迹吸附。")
            };
            var prepared = await Task.Run(() =>
            {
                var service = new MarkerTrackSnapService();
                var ownerId = marker is Waypoint ownedPoint ? ownedPoint.AssociatedTrackId : (marker as GuideNode)?.TrackId;
                var result = service.FindNearest(project, current, !nearestAnyTrack, token, ownerId);
                var preferred = marker is Waypoint w ? w.PreferredTrackPosition : (marker as GuideNode)?.PreferredTrackPosition;
                var position = _guideStructure.SnapNodeToPosition(result.Track,
                    marker is GuideNode node ? node.OriginalCoordinate : current, preferred);
                var segments = marker is GuideNode guideNode
                    ? service.PreviewGuideSegments(project, guideNode, position) : null;
                return (result.Track, Position: position,
                    DistanceFromMarker: result.Position.DistanceToTrack, Segments: segments,
                    Revision: result.Track.GeometryRevision);
            }, token);
            token.ThrowIfCancellationRequested();
            if(chooseBranch)
            {
                var candidateData=await Task.Run(()=>(Positions:new OverlapSnapService().Find(prepared.Track,current,token:token),Total:prepared.Track.GetTotalLength()),token);
                var picker=new SnapBranchWindow(prepared.Track,candidateData.Positions,candidateData.Total) {Owner=System.Windows.Application.Current.MainWindow};
                if(picker.ShowDialog()!=true || picker.Result is not { } chosen) return;
                if(marker is GuideNode chosenGuide) chosen=chosen with {DistanceToTrack=GeoDistanceCalculator.Instance.CalculateDistance(chosenGuide.OriginalCoordinate,chosen.SnappedCoordinate)};
                var segments=marker is GuideNode guide ? await Task.Run(()=>new MarkerTrackSnapService().PreviewGuideSegments(project,guide,chosen),token) : null;
                prepared=(prepared.Track,chosen,chosen.DistanceToTrack,segments,prepared.Revision);
            }
            if (!ReferenceEquals(CurrentProject, project) ||
                marker is Waypoint currentWaypoint && !project.Waypoints.Contains(currentWaypoint) ||
                marker is GuideNode currentGuide && !project.GuideNodes.Contains(currentGuide)) return;
            var warning = project.Settings.GuideSnap.AutoSnapWarningDistanceMeters;
            if (prepared.DistanceFromMarker > warning && MessageBox.Show(
                System.Windows.Application.Current.MainWindow,
                $"该标注距目标轨迹 {prepared.DistanceFromMarker:F1} 米，仍要吸附吗？",
                "确认较远吸附", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (!project.Tracks.Contains(prepared.Track) ||
                !MapLayerVisibility.IsTrackVisible(project.Settings.Map, prepared.Track.Id) ||
                prepared.Track.GeometryRevision != prepared.Revision ||
                new TrackSnapService().GetGeometryFingerprint(prepared.Track) !=
                    prepared.Position.TrackGeometryFingerprint ||
                !nearestAnyTrack && project.MainGuideTrackId != prepared.Track.Id) return;
            var staleBefore = project.IsAnalysisResultStale;
            if (marker is Waypoint waypoint)
            {
                var oldCoordinate = waypoint.Coordinate;
                var oldOwner = waypoint.AssociatedTrackId;
                var oldDem = waypoint.DemElevation;
                var oldPreferred = waypoint.PreferredTrackPosition;
                _history.Execute("普通标注吸附到轨迹", () => project.ExecuteAtomicUpdate(p =>
                {
                    waypoint.Latitude = prepared.Position.SnappedCoordinate.Latitude;
                    waypoint.Longitude = prepared.Position.SnappedCoordinate.Longitude;
                    waypoint.AssociatedTrackId = prepared.Track.Id;
                    waypoint.PreferredTrackPosition = prepared.Position;
                    waypoint.DemElevation = null;
                    p.IsAnalysisResultStale = true;
                    p.MarkDirty();
                }), () => project.ExecuteAtomicUpdate(p =>
                {
                    waypoint.Latitude = oldCoordinate.Latitude;
                    waypoint.Longitude = oldCoordinate.Longitude;
                    waypoint.AssociatedTrackId = oldOwner;
                    waypoint.PreferredTrackPosition = oldPreferred;
                    waypoint.DemElevation = oldDem;
                    p.IsAnalysisResultStale = staleBefore;
                    p.MarkDirty();
                }));
            }
            else if (marker is GuideNode guideNode)
            {
                var oldTrackId = guideNode.TrackId;
                var oldPosition = guideNode.ExactPosition;
                var oldPreferred = guideNode.PreferredTrackPosition;
                var oldCoordinate = guideNode.SnappedCoordinate;
                var oldDistance = guideNode.DistanceFromStart;
                var oldDistanceToTrack = guideNode.DistanceToTrack;
                var oldSegmentIndex = guideNode.TrackSegmentIndex;
                var oldFraction = guideNode.TrackPosition;
                var oldFingerprint = guideNode.TrackGeometryFingerprint;
                var oldOrphan = guideNode.IsOrphaned;
                var oldDem = guideNode.DemElevation;
                var oldSegments = project.GuideSegments.ToArray();
                _history.Execute("攻略节点吸附到攻略轨迹", () => project.ExecuteAtomicUpdate(p =>
                {
                    guideNode.TrackId = prepared.Track.Id;
                    _guideStructure.ApplyPosition(guideNode, prepared.Position);
                    guideNode.PreferredTrackPosition = chooseBranch ? prepared.Position : oldPreferred;
                    guideNode.DemElevation = null;
                    ReplaceGuideSegments(p, prepared.Segments!);
                    p.IsAnalysisResultStale = true;
                    p.NotifyGuideNodeChanged();
                }), () => project.ExecuteAtomicUpdate(p =>
                {
                    guideNode.TrackId = oldTrackId;
                    guideNode.ExactPosition = oldPosition;
                    guideNode.PreferredTrackPosition = oldPreferred;
                    guideNode.SnappedCoordinate = oldCoordinate;
                    guideNode.DistanceFromStart = oldDistance;
                    guideNode.DistanceToTrack = oldDistanceToTrack;
                    guideNode.TrackSegmentIndex = oldSegmentIndex;
                    guideNode.TrackPosition = oldFraction;
                    guideNode.TrackGeometryFingerprint = oldFingerprint;
                    guideNode.IsOrphaned = oldOrphan;
                    guideNode.DemElevation = oldDem;
                    ReplaceGuideSegments(p, oldSegments);
                    p.IsAnalysisResultStale = staleBefore;
                    p.NotifyGuideNodeChanged();
                }));
            }
            RefreshProjectView();
            SelectMarkerFromMapContext(marker);
            StatusText = $"已将标注吸附到“{prepared.Track.Name ?? "未命名轨迹"}”，距原位置 " +
                $"{prepared.DistanceFromMarker:F1} 米；可撤销。";
        });
    }

    private async Task ResnapActiveGuidesAsync()
    {
        var project = CurrentProject;
        var track = project?.MainGuideTrack;
        if (project is null || track is null) return;
        var nodes = project.GuideNodes.Where(node => node.TrackId == track.Id).ToArray();
        if (nodes.Length == 0) return;
        await RunOperationAsync("正在后台重新吸附攻略节点……", async token =>
        {
            var positions = await Task.Run(() => nodes.Select(node =>
            {
                token.ThrowIfCancellationRequested();
                return _guideStructure.SnapNodeToPosition(track, node.OriginalCoordinate, node.PreferredTrackPosition);
            }).ToArray(), token);
            var warningDistance = project.Settings.GuideSnap.AutoSnapWarningDistanceMeters;
            var farCount = positions.Count(position => position.DistanceToTrack > warningDistance);
            if (farCount > 0 && MessageBox.Show(System.Windows.Application.Current.MainWindow,
                    $"有 {farCount} 个攻略节点距轨迹超过 {warningDistance:F0} 米。仍要按这些位置重新吸附吗？",
                    "确认较远攻略节点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var oldPositions = nodes.Select(node => node.ExactPosition).ToArray();
            var oldOrphans = nodes.Select(node => node.IsOrphaned).ToArray();
            var oldSegments = project.GuideSegments.ToArray();
            GuideSegment[]? newSegments = null;
            _history.Execute("批量重新吸附攻略节点", () => project.ExecuteAtomicUpdate(p =>
            {
                for (var index = 0; index < nodes.Length; index++) _guideStructure.ApplyPosition(nodes[index], positions[index]);
                newSegments ??= _guideStructure.RebuildSegments(p).ToArray();
                ReplaceGuideSegments(p, newSegments);
                p.NotifyGuideNodeChanged();
            }), () => project.ExecuteAtomicUpdate(p =>
            {
                for (var index = 0; index < nodes.Length; index++)
                {
                    if (oldPositions[index] is { } old) _guideStructure.ApplyPosition(nodes[index], old);
                    else { nodes[index].ExactPosition = null; nodes[index].SnappedCoordinate = null; nodes[index].DistanceFromStart = null; nodes[index].DistanceToTrack = null; }
                    nodes[index].IsOrphaned = oldOrphans[index];
                }
                ReplaceGuideSegments(p, oldSegments);
                p.NotifyGuideNodeChanged();
            }));
            RefreshProjectView();
            StatusText = $"已重新吸附 {nodes.Length} 个攻略节点。";
        });
    }

    private async void OnSnapLocationPicked(object? sender, MapSnapLocationPick pick)
    {
        var project = CurrentProject;
        var node = project?.GuideNodes.FirstOrDefault(value => value.Id == pick.GuideNodeId);
        var track = project?.Tracks.FirstOrDefault(value => value.Id == node?.TrackId);
        if (project is null || node is null || track is null) return;
        try
        {
            var candidates = await Task.Run(() => new OverlapSnapService().Find(track, pick.Coordinate));
            if (candidates.Count == 0) throw new InvalidOperationException("该轨迹没有可用线段。");
            var window = new SnapCandidateWindow(candidates) { Owner = System.Windows.Application.Current.MainWindow };
            if (window.ShowDialog() != true || window.SelectedPosition is not { } selected) return;
            var position = selected with
            {
                DistanceToTrack = GeoDistanceCalculator.Instance.CalculateDistance(node.OriginalCoordinate, selected.SnappedCoordinate)
            };
            if (position.DistanceToTrack > project.Settings.GuideSnap.AutoSnapWarningDistanceMeters &&
                MessageBox.Show(window.Owner, $"所选位置距节点原始坐标 {position.DistanceToTrack:F1} 米，仍要使用吗？",
                    "确认较远攻略节点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var previous = node.ExactPosition;
            var previousOrphaned = node.IsOrphaned;
            var previousPreferred = node.PreferredTrackPosition;
            var oldSegments = project.GuideSegments.ToArray();
            GuideSegment[]? newSegments = null;
            _history.Execute("选择攻略节点轨迹位置", () => project.ExecuteAtomicUpdate(p =>
            {
                _guideStructure.ApplyPosition(node, position);
                node.PreferredTrackPosition = position;
                newSegments ??= _guideStructure.RebuildSegments(p).ToArray();
                ReplaceGuideSegments(p, newSegments);
                p.NotifyGuideNodeChanged();
            }), () => project.ExecuteAtomicUpdate(p =>
            {
                if (previous is not null) _guideStructure.ApplyPosition(node, previous);
                else { node.ExactPosition = null; node.SnappedCoordinate = null; node.DistanceFromStart = null; node.DistanceToTrack = null; }
                node.IsOrphaned = previousOrphaned;
                node.PreferredTrackPosition = previousPreferred;
                ReplaceGuideSegments(p, oldSegments);
                p.NotifyGuideNodeChanged();
            }));
            RefreshProjectView();
        }
        catch (Exception exception)
        {
            _log.Error(exception, "选择轨迹吸附位置失败");
            _dialogs.ShowError("选择轨迹位置失败", exception.Message);
        }
    }

    private Task EnterTrackEditAsync() => EnterTrackEditForAsync(_selectedModel as Track ??
        CurrentProject?.ActiveTrack ?? CurrentProject?.Tracks.FirstOrDefault());

    private async Task EnterTrackEditForAsync(Track? track)
    {
        if (!Map.IsReady)
        {
            _dialogs.ShowError("无法进入轨迹编辑", "地图尚未就绪，请稍后再试。");
            return;
        }
        if (CurrentProject is not { } project || track is null ||
            !project.Tracks.Contains(track))
        {
            _dialogs.ShowError("无法进入轨迹编辑", "请先选择要编辑的轨迹。");
            return;
        }
        if (_trackEditState.IsActive && ReferenceEquals(_trackEditProject, project))
        {
            _trackEditContextChangeTask = ActivateTrackEditTargetAsync(project, track);
            await _trackEditContextChangeTask;
            return;
        }
        _trackEditState.Begin(project, track);
        _trackEditPick = null;
        _trackRangeStart = null;
        _trackRangeEnd = null;
        _trackClickMode = TrackClickMode.SelectPoint;
        var session = _trackEditSessionId!.Value;
        await _mapRefreshTask;
        if (!_trackEditState.IsCurrent(CurrentProject, project.Id, session, track.Id, track.GeometryRevision)) return;
        await Map.BeginTrackEditAsync(track, session);
        StatusText = $"项目轨迹编辑模式：当前“{track.Name ?? "未命名轨迹"}”。可直接点选项目内其他轨迹继续编辑；点选线条选择真实轨迹点，拖动圆点后提交。";
        OnPropertyChanged(nameof(IsTrackEditActive));
        OnPropertyChanged(nameof(SelectedTrackEditPointId));
        RaiseCommandStates();
    }

    private async Task ActivateTrackEditTargetAsync(HikingProject project, Track track)
    {
        if (!_trackEditState.SelectTarget(project, track)) return;
        var session = _trackEditSessionId!.Value;
        // 不提交旧目标的点拖动或区间预览，也不调用 EndTrackEdit 退出项目模式。
        _trackEditPick = null;
        _trackRangeStart = null;
        _trackRangeEnd = null;
        _trackClickMode = TrackClickMode.SelectPoint;
        OnPropertyChanged(nameof(SelectedTrackEditPointId));
        RaiseCommandStates();
        await _mapRefreshTask;
        if (!_trackEditState.IsCurrent(CurrentProject, project.Id, session, track.Id, track.GeometryRevision)) return;
        await Map.BeginTrackEditAsync(track, session);
        if (_trackEditSessionId != session || !ReferenceEquals(_trackEditTrack, track)) return;
        StatusText = $"当前编辑轨迹：“{track.Name ?? "未命名轨迹"}”；可直接点选项目内其他轨迹，地图视角保持不变。";
        RaiseCommandStates();
    }

    /// <summary>地图右击先绑定目标，再构造操作菜单，避免拿旧会话编辑其他轨迹。</summary>
    public async Task<MapEventMessage?> PrepareTrackMapContextAsync(MapEventMessage value)
    {
        var project = CurrentProject;
        if (IsBusy || project is null || project.Id != value.ProjectId ||
            project.Tracks.FirstOrDefault(track => track.Id == value.TrackId &&
                track.GeometryRevision == value.GeometryRevision) is not { } track)
            return null;
        if (_trackEditState.IsActive && value.EditSessionId != _trackEditSessionId) return null;
        if (value.SplitSectionId is Guid sectionId)
        {
            if (track.GetSplitSections().All(section => section.Id != sectionId)) return null;
            // 分段事件已经由 MapViewModel 同步树和多选；这里只等待其目标绑定。
            if (_trackEditState.IsActive && !ReferenceEquals(_trackEditTrack, track))
                _trackEditContextChangeTask = ActivateTrackEditTargetAsync(project, track);
        }
        else SelectTrackFromMapContext(track);
        await _trackEditContextChangeTask;
        if (!ReferenceEquals(CurrentProject, project) || !project.Tracks.Contains(track) ||
            track.GeometryRevision != value.GeometryRevision) return null;
        if (!_trackEditState.IsActive) return value;
        return ReferenceEquals(_trackEditProject, project) && ReferenceEquals(_trackEditTrack, track)
            ? value with { EditSessionId = _trackEditSessionId } : null;
    }

    private async Task ExitTrackEditAsync()
    {
        _trackEditState.End();
        _trackEditContextChangeTask = Task.CompletedTask;
        _trackEditPick = null;
        _trackRangeStart = null;
        _trackRangeEnd = null;
        _trackClickMode = TrackClickMode.SelectPoint;
        await Map.EndTrackEditAsync();
        StatusText = "已退出轨迹编辑模式。";
        OnPropertyChanged(nameof(IsTrackEditActive));
        OnPropertyChanged(nameof(SelectedTrackEditPointId));
        RaiseCommandStates();
    }

    private void BeginTrackClickMode(TrackClickMode mode)
    {
        if (_trackEditSessionId is null) return;
        _trackClickMode = mode;
        if (mode == TrackClickMode.SelectRange)
        {
            _trackRangeStart = null;
            _trackRangeEnd = null;
        }
        StatusText = mode switch
        {
            TrackClickMode.InsertPoint => "请在目标轨迹线附近点击，将在完整轨迹线段中插入点击坐标。",
            TrackClickMode.SplitSegment => "请在需要拆分的轨迹段位置点击。",
            TrackClickMode.SelectRange => "请依次点击区间起点和终点。",
            _ => "请点击轨迹选择真实轨迹点。"
        };
        RaiseCommandStates();
    }

    public async Task CancelPendingTrackEditAsync()
    {
        if (_trackEditSessionId is not Guid session || _trackEditTrack is not { } track) return;
        _operationCancellation?.Cancel();
        _trackEditPick = null;
        _trackClickMode = TrackClickMode.SelectPoint;
        _trackRangeStart = null;
        _trackRangeEnd = null;
        if (_trackEditSessionId == session && ReferenceEquals(_trackEditTrack, track) &&
            _trackEditProject is { } project && project.Tracks.Contains(track))
        {
            _trackEditState.SelectTarget(project, track, renewSession: true);
            await Map.BeginTrackEditAsync(track, _trackEditSessionId!.Value);
        }
        StatusText = "已取消当前轨迹编辑预览；已提交的修改未撤销。";
        OnPropertyChanged(nameof(SelectedTrackEditPointId));
        RaiseCommandStates();
    }

    private bool CanEditSelectedSegment() => !IsBusy && !Map.TerrainCoordinateEditingBlocked&&_trackEditSessionId is not null &&
        _selectedModel is SplitSection section && _trackEditTrack?.Id == section.TrackId &&
        _trackEditTrack.GetSplitSections().Any(item => item.Id == section.Id);

    private Track? TargetSplitTrack() => CurrentProject?.Tracks.FirstOrDefault(track =>
        track.Id == _selectedSplitTrackId) ?? CurrentProject?.ActiveTrack ?? _trackEditTrack;

    private bool CanCombineSplitSections() => !IsBusy && CurrentProject is not null &&
        TargetSplitTrack()?.GetSplitSections().Count >= 2;

    private bool CanCombineGeometry() => !IsBusy && !Map.TerrainCoordinateEditingBlocked&&CurrentProject is { } project &&
        (project.Tracks.Count(t => t.Id != project.MainGuideTrackId) >= 2 ||
         project.Tracks.Any(t => t.Id != project.MainGuideTrackId && t.GetSplitSections().Count >= 2)) &&
        !SelectedTracks().Any(t => t.Id == project.MainGuideTrackId) &&
        !project.Tracks.Where(t => t.Id == project.MainGuideTrackId).SelectMany(t => t.GetSplitSections()).Any(s => _selectedSplitSectionIds.Contains(s.Id));

    private Task EditCurrentTrackAsync(string operation,
        Func<CancellationToken, TrackEditPlan> prepare) =>
        ApplyTrackEditAsync(operation, prepare, true);

    private Task EditSelectedSplitSectionAsync(string operation,
        Func<HikingProject, Track, SplitSection, CancellationToken, TrackEditPlan> prepare)
    {
        if (_trackEditProject is not { } project || _trackEditTrack is not { } track ||
            _selectedModel is not SplitSection section || section.TrackId != track.Id ||
            track.GetSplitSections().All(item => item.Id != section.Id))
            return Task.CompletedTask;
        return ApplyTrackEditAsync(operation, token => prepare(project, track, section, token), true);
    }

    private async Task CreateSplitFromSectionsAsync(bool connect)
    {
        var project = CurrentProject;
        var track = TargetSplitTrack();
        if (project is null || track is null || track.GetSplitSections().Count < 2) return;
        var previousSelection = _selectedSplitSectionIds.Where(id =>
            track.GetSplitSections().Any(section => section.Id == id)).ToArray();
        var dialog = new TrackMergeWindow(track, connect, previousSelection.ToHashSet(),
            geometry => _ = Map.FocusObjectAsync(geometry),
            coordinate => _ = Map.FocusCoordinateAsync(coordinate))
        { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        var sources = dialog.SelectedSplitSources;
        await RunOperationAsync(connect ? "正在准备连接拆分分段……" : "正在准备合并拆分分段……",
            async token =>
            {
                var plan = await Task.Run(() => _trackEdits.PrepareCombineSplitSections(
                    project, track.Id, sources, connect, token), token);
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(CurrentProject, project) || !project.Tracks.Contains(track)) return;
                var impact = $"轨迹长度：{plan.Impact.DistanceBefore / 1000:F2} → " +
                    $"{plan.Impact.DistanceAfter / 1000:F2} km\n" +
                    $"受影响攻略节点：{plan.Impact.AffectedGuideNodes}；失联：{plan.Impact.OrphanedGuideNodes}\n" +
                    string.Join("\n", plan.Warnings) + "\n\n是否应用修改？";
                if (MessageBox.Show(System.Windows.Application.Current.MainWindow, impact,
                    connect ? "确认连接拆分分段" : "确认合并拆分分段",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                var oldIds = track.GetSplitSections().Select(section => section.Id).ToHashSet();
                Guid mergedId = Guid.Empty;
                _history.Execute(connect ? "连接拆分分段" : "合并拆分分段", () =>
                {
                    plan.Apply();
                    if (mergedId == Guid.Empty)
                        mergedId = track.GetSplitSections().Select(section => section.Id)
                            .First(id => !oldIds.Contains(id));
                    _selectedSplitTrackId = track.Id;
                    _selectedSplitSectionIds.Clear();
                    _selectedSplitSectionIds.Add(mergedId);
                    _selectedNavigationKeys.RemoveWhere(key => key.StartsWith("拆分分段:", StringComparison.Ordinal));
                    _selectedNavigationKeys.Add($"拆分分段:{mergedId:D}");
                }, () =>
                {
                    plan.Undo();
                    _selectedSplitTrackId = track.Id;
                    _selectedSplitSectionIds.Clear();
                    _selectedSplitSectionIds.UnionWith(sources.Select(source => source.SectionId));
                    _selectedNavigationKeys.RemoveWhere(key => key.StartsWith("拆分分段:", StringComparison.Ordinal));
                    foreach (var source in sources) _selectedNavigationKeys.Add($"拆分分段:{source.SectionId:D}");
                });
                Map.InvalidateTrack(track.Id);
                RefreshProjectView();
                if (_mapNodes.TryGetValue(mergedId, out var selectedNode))
                    SelectNavigationNode(selectedNode, false);
                StatusText = connect ? "拆分分段已按面板顺序和方向连接，可撤销。" :
                    "拆分分段已合并，空间间隙保持空白，可撤销。";
            });
    }

    private Task EditSelectedRangeAsync(bool crop)
    {
        if (_trackEditProject is not { } project || _trackEditTrack is not { } track ||
            _trackRangeStart is not { } start || _trackRangeEnd is not { } end)
            return Task.CompletedTask;
        var range = new TrackRange(track.Id, start, end);
        return ApplyTrackEditAsync(crop ? "保留所选区间" : "删除所选区间",
            token => crop ? _trackEdits.PrepareCropRange(project, range, token)
                : _trackEdits.PrepareDeleteRange(project, range, token), true);
    }

    private async Task CreateTrackFromTracksAsync(bool connect)
    {
        var project = CurrentProject;
        if (project is null || !CanCombineGeometry()) return;
        var selected = SelectedTracks().Select(track => new TrackMergeSource(track.Id, false))
            .Concat(project.Tracks.SelectMany(track => track.GetSplitSections()
                .Where(section => _selectedSplitSectionIds.Contains(section.Id))
                .Select(section => new TrackMergeSource(track.Id, false, section.Id)))).ToArray();
        var dialog = new TrackMergeWindow(project, connect, null,
            geometry => _ = Map.FocusObjectAsync(geometry),
            coordinate => _ = Map.FocusCoordinateAsync(coordinate),
            selected.Length == 0 ? null : selected, restrictToSelection:IsTrackEditActive)
        { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        var sources = dialog.SelectedSources;
        var name = dialog.NewTrackName;
        var deleteOriginals = dialog.DeleteOriginalTracks;
        await RunOperationAsync(connect ? "正在准备连接轨迹……" : "正在准备合并轨迹……",
            async token =>
            {
                var plan = await Task.Run(() => new TrackMergeService().Prepare(project, sources,
                    name, connect, token, deleteOriginals), token);
                plan.NewTrack.Metadata[HikingGuide.Core.Services.TrackKilometerService.RouteKindKey]=dialog.IsSingleJourney?"route":"collection";
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(CurrentProject, project)) return;
                if (connect || deleteOriginals)
                {
                    var detail = string.Join("\n", plan.Connectors.Select(item =>
                        $"直线连接：{item.LengthMeters:F1} m"));
                    if (connect) detail += "\n这些连接不是道路或徒步路线，且不会调用 BRouter。";
                    if (deleteOriginals)
                    {
                        detail += "\n\n将从项目中删除以下所选来源：\n" +
                            string.Join("\n", plan.Removals.Select(removal => removal.WholeTrack
                                ? $"• {removal.Track.Name ?? "未命名轨迹"}：整条轨迹（已选中全部范围）"
                                : $"• {removal.Track.Name ?? "未命名轨迹"}：仅 {string.Join("、", removal.Sections.Select(section => section.Name))}；其他分段保留")) +
                            "\n删除整轨时所属普通标注转到新轨迹；删除部分分段时按原轨迹最近位置，移除分段的标注转到新轨迹，其余仍归属保留轨迹。图片及人工内容保留；原始文件不删除。";
                        if (plan.Removals.Any(removal => removal.WholeTrack && removal.Track.Id == project.MainGuideTrackId))
                            detail += "\n包含主攻略轨迹：删除后项目暂时没有主攻略轨迹，攻略节点保留为未关联状态；不会自动将新轨迹设为攻略轨迹。";
                    }
                    if (!AutoConfirmTrackEditForAutomation && MessageBox.Show(System.Windows.Application.Current.MainWindow,
                            detail + "\n\n是否创建新轨迹并执行以上操作？",
                            connect ? "确认连接轨迹" : "确认合并轨迹", MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                }
                _history.Execute(connect ? "创建连接轨迹" : "创建合并轨迹", plan.Apply, plan.Undo);
                foreach (var source in plan.SourceTracks) Map.InvalidateTrack(source.Id);
                _guideStructure.ClearSnapCache();
                RefreshProjectView();
                StatusText = $"已创建轨迹“{plan.NewTrack.Name}”；" +
                    (deleteOriginals ? "所选来源已删除，未选分段、标注及人工内容保留，可撤销。" : "原轨迹与标注保持不变，可撤销。");
                _log.Information($"[TrackMerge] ProjectId={project.Id}; NewTrackId={plan.NewTrack.Id}; " +
                    $"Sources={sources.Count}; Connectors={plan.Connectors.Count}; " +
                    $"Points={plan.NewTrack.GetPointCount()}; Length={plan.NewTrack.GetTotalLength():F1}");
            });
    }

    private bool IsCurrentTrackEdit(MapEventMessage value) =>
        _trackEditState.IsCurrent(CurrentProject, value.ProjectId, value.EditSessionId,
            value.TrackId, value.GeometryRevision);

    private async Task SplitTrackAtMapContextAsync(MapEventMessage value)
    {
        if (_trackEditProject?.MainGuideTrackId == _trackEditTrack?.Id)
        {
            _dialogs.ShowWarning("不可拆分攻略轨迹", "攻略轨迹不可拆分，如要拆分请设为普通轨迹后，再操作。");
            return;
        }
        if (!IsCurrentTrackEdit(value) || IsBusy || _trackEditTrack is not { } track ||
            value.Latitude is not double latitude || value.Longitude is not double longitude ||
            value.Zoom is not double zoom) return;
        var coordinate = new GeoCoordinate(latitude, longitude);
        var session = _trackEditSessionId;
        var section = value.SplitSectionId is Guid sectionId
            ? track.GetSplitSections().FirstOrDefault(item => item.Id == sectionId) : null;
        if (value.SplitSectionId is not null && section is null) return;
        // 分段右击时仅在该分段的完整物理 Part 上拾取，避免交叉轨迹误拆到另一段。
        var pickTrack = section is null ? track : new Track
        {
            Id = track.Id, Segments = section.GeometryParts.ToList()
        };
        TrackPointEditPick? picked = null;
        await RunOperationAsync("正在定位右击处的完整轨迹……", async token =>
        {
            picked = await Task.Run(() => _trackEditIndex.Pick(pickTrack, coordinate, zoom), token);
        });
        if (!IsCurrentTrackEdit(value) || session != _trackEditSessionId) return;
        if (picked is null)
        {
            StatusText = "右击位置离完整轨迹太远，未执行拆分。";
            return;
        }
        var position = picked.LinePosition;
        var edgeIndex = position.Fraction == 0 && position.StartPointIndex > 0
            ? position.StartPointIndex - 1 : position.StartPointIndex;
        var fraction = edgeIndex == position.StartPointIndex ? position.Fraction : 1;
        await ApplyTrackEditAsync("拆分轨迹段", token =>
            _trackEdits.PrepareSplitSegment(_trackEditProject!, track.Id,
                position.TrackSegmentId, edgeIndex, fraction, token), true);
        _trackClickMode = TrackClickMode.SelectPoint;
    }

    private async void OnMapTrackEditRequested(object? sender, MapEventMessage value)
    {
        try
        {
            if (value.Type == "TrackEditPreviewCancelled")
            {
                if (IsCurrentTrackEdit(value)) await CancelPendingTrackEditAsync();
                return;
            }
            if (value.Type == "TrackEditLineClicked")
            {
                // 点选允许使用当前项目会话选择另一条轨迹；拖动则始终严格绑定当前目标。
                if (IsBusy || _trackEditState.ResolveSelectionTarget(CurrentProject, value.ProjectId,
                        value.EditSessionId, value.TrackId, value.GeometryRevision) is not { } selectedTrack ||
                    value.Latitude is null || value.Longitude is null || CurrentProject is not { } selectedProject)
                    return;
                if (value.SplitSectionId is Guid sectionId &&
                    selectedTrack.GetSplitSections().All(section => section.Id != sectionId)) return;
                var selectedId = value.SplitSectionId ?? selectedTrack.Id;
                if (!_mapNodes.TryGetValue(selectedId, out var selectedNode)) return;
                var oldSession = _trackEditSessionId;
                _isMapSelectionInProgress = true;
                try
                {
                    UpdateProjectItemSelection(selectedNode, value.CtrlKey ? ModifierKeys.Control : ModifierKeys.None,
                        focusOnMap: false);
                }
                finally { _isMapSelectionInProgress = false; }
                await _trackEditContextChangeTask;
                if (!ReferenceEquals(CurrentProject, selectedProject) ||
                    !ReferenceEquals(_trackEditTrack, selectedTrack) ||
                    selectedTrack.GeometryRevision != value.GeometryRevision) return;
                // 该次合法点选的目标切换已轮换会话，仅重绑本次点击；其他旧消息不会被重绑。
                if (oldSession != _trackEditSessionId)
                    value = value with { EditSessionId = _trackEditSessionId };
                // Ctrl 多选只改变选择，不能误触发当前插点、拆分或区间编辑工具。
                if (value.CtrlKey) return;
            }
            if (!IsCurrentTrackEdit(value) || IsBusy || _trackEditTrack is not { } track ||
                value.Latitude is not double latitude || value.Longitude is not double longitude) return;
            var coordinate = new GeoCoordinate(latitude, longitude);
            if (value.Type == "TrackEditLineClicked")
            {
                var zoom = value.Zoom ?? 15;
                var session = _trackEditSessionId;
                var mode = _trackClickMode;
                TrackPointEditPick? picked = null;
                await RunOperationAsync("正在选择完整轨迹上的点……", async token =>
                {
                    // 交叉处分段点选只在其原始连续 Part 中拾取，不误编辑同轨迹的其他分段。
                    var pick = await Task.Run(() => PickTrackEditPoint(track, coordinate, zoom,
                        value.SplitSectionId), token);
                    if (!IsCurrentTrackEdit(value) || session != _trackEditSessionId) return;
                    picked = pick;
                    if (pick is null) StatusText = "请在要编辑的轨迹附近点击。";
                    else if (mode == TrackClickMode.SelectPoint)
                    {
                        _trackEditPick = pick;
                        OnPropertyChanged(nameof(SelectedTrackEditPointId));
                        await Map.SetTrackEditHandlesAsync(track, session!.Value, pick.VisibleHandles);
                        StatusText = $"已选择轨迹点 {pick.SelectedPoint.PointIndex + 1}；可拖动，或使用“轨迹 → 删除轨迹点”。";
                    }
                    RaiseCommandStates();
                });
                if (picked is null || !IsCurrentTrackEdit(value) || _trackClickMode != mode) return;
                if (mode == TrackClickMode.InsertPoint)
                {
                    var position = picked.LinePosition;
                    await ApplyTrackEditAsync("添加轨迹点", token =>
                        _trackEdits.PrepareInsertPoint(_trackEditProject!, track.Id,
                            position.TrackSegmentId, position.StartPointIndex, coordinate, token), false);
                    _trackClickMode = TrackClickMode.SelectPoint;
                }
                else if (mode == TrackClickMode.SplitSegment)
                {
                    if (_trackEditProject?.MainGuideTrackId == track.Id)
                    {
                        _dialogs.ShowWarning("不可拆分攻略轨迹", "攻略轨迹不可拆分，如要拆分请设为普通轨迹后，再操作。");
                        _trackClickMode = TrackClickMode.SelectPoint;
                        return;
                    }
                    var position = picked.LinePosition;
                    var edgeIndex = position.Fraction == 0 && position.StartPointIndex > 0
                        ? position.StartPointIndex - 1 : position.StartPointIndex;
                    var fraction = edgeIndex == position.StartPointIndex ? position.Fraction : 1;
                    await ApplyTrackEditAsync("拆分轨迹段", token =>
                        _trackEdits.PrepareSplitSegment(_trackEditProject!, track.Id,
                            position.TrackSegmentId, edgeIndex, fraction, token), true);
                    _trackClickMode = TrackClickMode.SelectPoint;
                }
                else if (mode == TrackClickMode.SelectRange)
                {
                    if (_trackRangeStart is null)
                    {
                        _trackRangeStart = picked.LinePosition;
                        StatusText = $"区间起点：{picked.LinePosition.DistanceFromTrackStart / 1000:F2} km。请点击终点。";
                    }
                    else
                    {
                        _trackRangeEnd = picked.LinePosition;
                        _trackClickMode = TrackClickMode.SelectPoint;
                        var from = Math.Min(_trackRangeStart.DistanceFromTrackStart,
                            _trackRangeEnd.DistanceFromTrackStart);
                        var to = Math.Max(_trackRangeStart.DistanceFromTrackStart,
                            _trackRangeEnd.DistanceFromTrackStart);
                        var range = new TrackRange(track.Id, _trackRangeStart, _trackRangeEnd);
                        await RunOperationAsync("正在准备区间预览……", async token =>
                        {
                            var preview = await Task.Run(() =>
                                _trackRangePreview.Build(track, range, zoom, token), token);
                            if (!IsCurrentTrackEdit(value) || session != _trackEditSessionId ||
                                _trackRangeStart is null || _trackRangeEnd is null) return;
                            await Map.PreviewTrackRangeAsync(track, session!.Value, preview);
                            StatusText = $"已选择 {from / 1000:F2} → {to / 1000:F2} km，长度 {(to - from) / 1000:F2} km；" +
                                $"涉及 {preview.SegmentCount} 个轨迹段、{preview.PointCount:N0} 个轨迹点。";
                        });
                        RaiseCommandStates();
                    }
                }
            }
            else if (value.Type == "TrackPointDragEnded" && value.TrackPointId is Guid pointId)
                await ApplyTrackEditAsync("移动轨迹点", token =>
                    _trackEdits.PrepareMovePoint(_trackEditProject!, track.Id, pointId, coordinate, token),
                    confirmImpact: false);
        }
        catch (Exception exception)
        {
            _log.Error(exception, "[TrackPointEdit] 地图编辑事件失败");
            _dialogs.ShowError("轨迹修改失败", "轨迹修改失败，原轨迹未发生改变。");
        }
    }

    private async Task DeleteSelectedTrackPointAsync()
    {
        if (_trackEditProject is not { } project || _trackEditTrack is not { } track ||
            _trackEditPick is not { } pick) return;
        var segment = track.Segments[pick.SelectedPoint.SegmentIndex];
        if (segment.Points.Count == 2)
        {
            if (MessageBox.Show(System.Windows.Application.Current.MainWindow,
                    "删除该点会使轨迹段失效。是否改为删除整个轨迹段？",
                    "确认删除轨迹段", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await ApplyTrackEditAsync("删除轨迹段", token =>
                _trackEdits.PrepareDeleteSegment(project, track.Id, segment.Id, token), true);
            return;
        }
        await ApplyTrackEditAsync("删除轨迹点", token =>
            _trackEdits.PrepareDeletePoint(project, track.Id, pick.SelectedPoint.PointId, token), true);
    }

    private TrackPointEditPick? PickTrackEditPoint(Track track, GeoCoordinate coordinate,
        double zoom, Guid? sectionId)
    {
        if (sectionId is null) return _trackEditIndex.Pick(track, coordinate, zoom);
        var section = track.GetSplitSections().FirstOrDefault(item => item.Id == sectionId);
        if (section is null) return null;
        if (section.GeometryParts.Count == track.Segments.Count &&
            section.GeometryParts.Select(part => part.Id).SequenceEqual(track.Segments.Select(part => part.Id)))
            return _trackEditIndex.Pick(track, coordinate, zoom);
        var pick = _trackEditIndex.Pick(new Track
        { Id = track.Id, Segments = section.GeometryParts.ToList() }, coordinate, zoom);
        if (pick is null) return null;
        // 子集索引只用于空间命中；所有后续编辑引用必须还原到完整 Working Track。
        var segmentIndex = track.Segments.FindIndex(part => part.Id == pick.LinePosition.TrackSegmentId);
        if (segmentIndex < 0) return null;
        var offset = track.Segments.Take(segmentIndex).Sum(part => part.GetLength());
        var position = pick.LinePosition with
        {
            SegmentIndex = segmentIndex,
            DistanceFromTrackStart = offset + pick.LinePosition.DistanceFromSegmentStart,
            TrackGeometryFingerprint = new TrackSnapService().GetGeometryFingerprint(track)
        };
        var handles = pick.VisibleHandles.Select(handle => handle with { SegmentIndex = segmentIndex }).ToArray();
        return new TrackPointEditPick(position,
            pick.SelectedPoint with { SegmentIndex = segmentIndex }, handles);
    }

    private async Task ApplyTrackEditAsync(string operation,
        Func<CancellationToken, TrackEditPlan> prepare, bool confirmImpact)
    {
        var project = _trackEditProject;
        var track = _trackEditTrack;
        var session = _trackEditSessionId;
        if (project is null || track is null || session is null) return;
        await RunOperationAsync($"正在准备{operation}……", async token =>
        {
            var plan = await Task.Run(() => prepare(token), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(CurrentProject, project) ||
                !ReferenceEquals(_trackEditTrack, track) || _trackEditSessionId != session) return;
            if (confirmImpact || plan.Impact.OrphanedGuideNodes > 0 || plan.Warnings.Count > 0)
            {
                var impact = $"轨迹长度：{plan.Impact.DistanceBefore / 1000:F2} → " +
                    $"{plan.Impact.DistanceAfter / 1000:F2} km\n" +
                    $"受影响攻略节点：{plan.Impact.AffectedGuideNodes}\n" +
                    $"其中失联：{plan.Impact.OrphanedGuideNodes}\n" +
                    $"攻略分段：{plan.Impact.GuideSegmentsBefore} → {plan.Impact.GuideSegmentsAfter}\n" +
                    string.Join("\n", plan.Warnings) + "\n\n" +
                    "是否应用修改？";
                if (!AutoConfirmTrackEditForAutomation &&
                    MessageBox.Show(System.Windows.Application.Current.MainWindow, impact,
                        $"确认{operation}", MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(CurrentProject, project) || !project.Tracks.Contains(track) ||
                !ReferenceEquals(_trackEditTrack, track) || _trackEditSessionId != session) return;
            _history.Execute(operation, plan.Apply, plan.Undo);
            _trackEditIndex.Invalidate(track.Id);
            _trackEditPick = null;
            _trackRangeStart = null;
            _trackRangeEnd = null;
            _trackEditState.SelectTarget(project, track, renewSession: true);
            session = _trackEditSessionId;
            RefreshProjectView(false);
            await Map.RefreshEditedTrackAsync(track);
            if (_trackEditSessionId == session && ReferenceEquals(CurrentProject, project))
                await Map.UpdateTrackEditContextAsync(track, session.Value);
            StatusText = $"{operation}已完成，可撤销。";
            _log.Information($"[TrackEdit] TrackId={track.Id}; Operation={operation}; " +
                $"Points={plan.PointsBefore}->{plan.PointsAfter}; Segments={plan.SegmentsBefore}->{plan.SegmentsAfter}; " +
                $"Length={plan.Impact.DistanceBefore:F1}->{plan.Impact.DistanceAfter:F1}; " +
                $"AffectedGuides={plan.Impact.AffectedGuideNodes}; Orphaned={plan.Impact.OrphanedGuideNodes}");
        });
    }

    private async void OnMapMarkerEditRequested(object? sender, MapMarkerEditRequest request)
    {
        var project = CurrentProject;
        if (project is null || IsBusy) return;
        try
        {
            request.Coordinate.Validate();
            if (request.Kind is MapMarkerEditKind.AddWaypoint or MapMarkerEditKind.MoveWaypoint)
            {
                var waypoint = request.Kind == MapMarkerEditKind.AddWaypoint
                    ? new Waypoint { Name = "新标注", Latitude = request.Coordinate.Latitude,
                        Longitude = request.Coordinate.Longitude, AssociatedTrackId = project.ActiveTrackId }
                    : project.Waypoints.FirstOrDefault(w => w.Id == request.ObjectId);
                if (waypoint is null) return;
                var previous = waypoint.Coordinate;
                var previousPreferred = waypoint.PreferredTrackPosition;
                if (request.Kind == MapMarkerEditKind.MoveWaypoint && previous == request.Coordinate) return;
                if (request.Kind == MapMarkerEditKind.AddWaypoint)
                    _history.Execute("添加普通标注", () => project.Waypoints.Add(waypoint), () => project.Waypoints.Remove(waypoint));
                else _history.Execute("移动普通标注", () => SetWaypointCoordinate(project, waypoint, request.Coordinate),
                    () => SetWaypointCoordinate(project, waypoint, previous, previousPreferred));
                RefreshProjectView();
                if (request.Kind == MapMarkerEditKind.AddWaypoint) await EditMarkerAsync(waypoint);
                return;
            }

            var existing = request.Kind == MapMarkerEditKind.MoveGuideNode
                ? project.GuideNodes.FirstOrDefault(n => n.Id == request.ObjectId) : null;
            var track = existing?.TrackId is Guid trackId
                ? project.Tracks.FirstOrDefault(t => t.Id == trackId) : project.MainGuideTrack;
            if (track is null)
            {
                _dialogs.ShowError("无法添加攻略节点", "请先设置主攻略轨迹。");
                await Map.NotifyProjectDataChangedAsync();
                return;
            }
            var snapped = await Task.Run(() => _guideStructure.SnapNodeToPosition(track, request.Coordinate));
            if (snapped.DistanceToTrack > project.Settings.GuideSnap.AutoSnapWarningDistanceMeters &&
                MessageBox.Show(System.Windows.Application.Current.MainWindow,
                    $"该位置距轨迹 {snapped.DistanceToTrack:F1} 米，仍要关联为攻略节点吗？",
                    "确认较远攻略节点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                await Map.NotifyProjectDataChangedAsync();
                return;
            }
            var node = existing ?? new GuideNode { Name = "新攻略节点", TrackId = track.Id, StableOrder = project.GuideNodes.Count };
            var oldCoordinate = node.OriginalCoordinate;
            var oldPosition = node.ExactPosition;
            var oldPreferred = node.PreferredTrackPosition;
            var oldOrphaned = node.IsOrphaned;
            var oldSegments = project.GuideSegments.ToArray();
            GuideSegment[]? newSegments = null;
            _history.Execute(existing is null ? "添加攻略节点" : "移动攻略节点", () => project.ExecuteAtomicUpdate(p =>
            {
                node.OriginalCoordinate = request.Coordinate;
                node.PreferredTrackPosition = null;
                _guideStructure.ApplyPosition(node, snapped);
                if (existing is null && !p.GuideNodes.Contains(node)) p.GuideNodes.Add(node);
                newSegments ??= _guideStructure.RebuildSegments(p).ToArray();
                ReplaceGuideSegments(p, newSegments);
                p.NotifyGuideNodeChanged();
            }), () => project.ExecuteAtomicUpdate(p =>
            {
                if (existing is null) p.GuideNodes.Remove(node);
                else
                {
                    node.OriginalCoordinate = oldCoordinate;
                    node.PreferredTrackPosition = oldPreferred;
                    if (oldPosition is not null) _guideStructure.ApplyPosition(node, oldPosition);
                    else { node.ExactPosition = null; node.SnappedCoordinate = null; node.DistanceFromStart = null; node.DistanceToTrack = null; }
                    node.IsOrphaned = oldOrphaned;
                }
                ReplaceGuideSegments(p, oldSegments);
                p.NotifyGuideNodeChanged();
            }));
            RefreshProjectView();
            if (existing is null) await EditMarkerAsync(node);
        }
        catch (Exception exception)
        {
            _log.Error(exception, "地图标注编辑失败");
            _dialogs.ShowError("标注编辑失败", exception.Message);
            await Map.NotifyProjectDataChangedAsync();
        }
    }

    private static void SetWaypointCoordinate(HikingProject project, Waypoint waypoint, HikingGuide.Core.Geometry.GeoCoordinate coordinate, TrackPosition? preferred=null)
    {
        project.ExecuteAtomicUpdate(p =>
        {
            waypoint.Latitude = coordinate.Latitude;
            waypoint.Longitude = coordinate.Longitude;
            waypoint.PreferredTrackPosition = preferred;
            p.MarkDirty();
        });
    }

    private void ChangeHistory(bool redo)
    {
        try { if (redo ? _history.Redo() : _history.Undo()) RefreshProjectView(); }
        catch (InvalidOperationException error)
        {
            StatusText = error.Message;
            _log.Warning(error.Message);
            if (!AutoConfirmTrackEditForAutomation) _dialogs.ShowWarning(redo ? "无法重做" : "无法撤销", error.Message);
        }
    }

    private async void BatchEditMarkers()
    {
        if (CurrentProject is not { } project) return;
        var markers = project.Waypoints.Cast<MapMarkerBase>().Concat(project.GuideNodes).Where(_selectedProjectItems.Contains).ToArray();
        if (markers.Length == 0) return;
        var window = new BatchMarkerEditorWindow(markers, project) { Owner = System.Windows.Application.Current.MainWindow };
        if (window.ShowDialog() != true || window.Result is not { } patch || !ReferenceEquals(CurrentProject, project)) return;
        IsBusy = true;
        try
        {
            var plan = await Task.Run(() => new BatchMarkerEdit().Prepare(project, markers, patch));
            if (!ReferenceEquals(CurrentProject, project)) return;
            var far = plan.FarCount(project.Settings.GuideSnap.AutoSnapWarningDistanceMeters);
            if (far > 0 && !AutoConfirmTrackEditForAutomation && MessageBox.Show(window.Owner,
                $"有 {far} 个标注距主攻略轨迹较远，仍要设为攻略节点吗？", "确认较远攻略节点",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _history.Execute("批量编辑标注或攻略节点", plan.Apply, plan.Undo);
            RefreshProjectView();
            StatusText = $"已批量修改 {markers.Length} 个点；未勾选字段、图片与人工内容保持不变，可撤销。";
        }
        catch (Exception exception)
        {
            _log.Error(exception, "批量编辑标注失败");
            StatusText = $"批量编辑失败：{exception.Message}";
            if (!AutoConfirmTrackEditForAutomation) MessageBox.Show(window.Owner, StatusText, "无法应用");
        }
        finally { IsBusy = false; }
    }

    private async Task EditMarkerAsync(MapMarkerBase? marker, bool? requestedGuide = null, Guid? requestedTrackId = null)
    {
        var project = CurrentProject;
        if (project is null || marker is not Waypoint and not GuideNode) return;
        var editor = new MarkerEditorWindow(marker, project, requestedGuide, requestedTrackId) { Owner = System.Windows.Application.Current.MainWindow };
        if (editor.ShowDialog() != true || editor.Result is not { } choice) return;
        var wasGuide = marker is GuideNode;
        var previousTrackId = (marker as GuideNode)?.TrackId;
        if (marker.Name == choice.Name && marker.Description == choice.Description && marker.Type == choice.Type &&
            marker.CustomTypeName == choice.CustomTypeName && wasGuide == choice.IsGuideNode &&
            (choice.IsGuideNode ? previousTrackId == choice.TrackId :
                (marker as Waypoint)?.AssociatedTrackId == choice.TrackId)) return;
        try
        {
            TrackPosition? newPosition = null;
            if (choice.IsGuideNode && (!wasGuide || previousTrackId != choice.TrackId ||
                (marker as GuideNode)?.ExactPosition is null || (marker as GuideNode)?.IsOrphaned == true))
            {
                var track = project.Tracks.First(t => t.Id == choice.TrackId);
                var coordinate = marker is Waypoint waypoint ? waypoint.PreservedOriginalCoordinate ?? waypoint.Coordinate
                    : ((GuideNode)marker).OriginalCoordinate;
                newPosition = await Task.Run(() => _guideStructure.SnapNodeToPosition(track, coordinate,
                    marker is Waypoint owned ? owned.PreferredTrackPosition : (marker as GuideNode)?.PreferredTrackPosition));
                if (newPosition.DistanceToTrack > project.Settings.GuideSnap.AutoSnapWarningDistanceMeters &&
                    MessageBox.Show(editor.Owner, $"该节点距轨迹 {newPosition.DistanceToTrack:F1} 米，仍要保存吗？",
                        "确认较远攻略节点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            var oldName = marker.Name;
            var oldDescription = marker.Description;
            var oldType = marker.Type;
            var oldCustomType = marker.CustomTypeName;
            var oldSegments = project.GuideSegments.ToArray();
            GuideSegment[]? newSegments = null;
            if (marker is Waypoint existingWaypoint && choice.IsGuideNode)
            {
                var node = existingWaypoint.ToGuideNode();
                node.SourceTrackName = existingWaypoint.AssociatedTrackId is Guid ownerId
                    ? project.Tracks.FirstOrDefault(track => track.Id == ownerId)?.Name ?? "（未命名轨迹）" : null;
                node.TrackId = choice.TrackId;
                node.StableOrder = project.GuideNodes.Count;
                _guideStructure.ApplyPosition(node, newPosition!);
                SetMarkerFields(node, choice);
                _history.Execute("普通标注转攻略节点", () => project.ExecuteAtomicUpdate(p =>
                {
                    p.Waypoints.Remove(existingWaypoint);
                    p.GuideNodes.Add(node);
                    SetMediaOwnerType(p, node.Id, MediaOwnerType.GuideNode);
                    newSegments ??= _guideStructure.RebuildSegments(p).ToArray();
                    ReplaceGuideSegments(p, newSegments);
                }), () => project.ExecuteAtomicUpdate(p =>
                {
                    p.GuideNodes.Remove(node);
                    p.Waypoints.Add(existingWaypoint);
                    SetMediaOwnerType(p, node.Id, MediaOwnerType.Waypoint);
                    ReplaceGuideSegments(p, oldSegments);
                }));
            }
            else if (marker is GuideNode existingNode && !choice.IsGuideNode)
            {
                var waypoint = existingNode.ToWaypoint(project);
                waypoint.AssociatedTrackId = choice.TrackId;
                SetMarkerFields(waypoint, choice);
                _history.Execute("攻略节点转普通标注", () => project.ExecuteAtomicUpdate(p =>
                {
                    p.GuideNodes.Remove(existingNode);
                    p.Waypoints.Add(waypoint);
                    SetMediaOwnerType(p, waypoint.Id, MediaOwnerType.Waypoint);
                    newSegments ??= _guideStructure.RebuildSegments(p).ToArray();
                    ReplaceGuideSegments(p, newSegments);
                }), () => project.ExecuteAtomicUpdate(p =>
                {
                    p.Waypoints.Remove(waypoint);
                    p.GuideNodes.Add(existingNode);
                    SetMediaOwnerType(p, waypoint.Id, MediaOwnerType.GuideNode);
                    ReplaceGuideSegments(p, oldSegments);
                }));
            }
            else
            {
                var oldPosition = (marker as GuideNode)?.ExactPosition;
                var oldOwner = (marker as Waypoint)?.AssociatedTrackId;
                _history.Execute("编辑标注", () => project.ExecuteAtomicUpdate(p =>
                {
                    SetMarkerFields(marker, choice);
                    if (marker is Waypoint point) point.AssociatedTrackId = choice.TrackId;
                    if (marker is GuideNode guide && newPosition is not null)
                    {
                        guide.TrackId = choice.TrackId;
                        _guideStructure.ApplyPosition(guide, newPosition);
                    }
                    newSegments ??= _guideStructure.RebuildSegments(p).ToArray();
                    ReplaceGuideSegments(p, newSegments);
                    p.MarkDirty();
                }), () => project.ExecuteAtomicUpdate(p =>
                {
                    marker.Name = oldName; marker.Description = oldDescription;
                    marker.Type = oldType; marker.CustomTypeName = oldCustomType;
                    if (marker is Waypoint point) point.AssociatedTrackId = oldOwner;
                    if (marker is GuideNode guide)
                    {
                        guide.TrackId = previousTrackId;
                        if (oldPosition is not null) _guideStructure.ApplyPosition(guide, oldPosition);
                    }
                    ReplaceGuideSegments(p, oldSegments);
                    p.MarkDirty();
                }));
            }
            RefreshProjectView();
        }
        catch (Exception exception)
        {
            _log.Error(exception, "编辑标注失败");
            _dialogs.ShowError("编辑标注失败", exception.Message);
        }
    }

    private static void SetMarkerFields(MapMarkerBase marker, MarkerEditResult choice)
    {
        marker.Name = choice.Name;
        marker.Description = choice.Description;
        marker.Type = choice.Type;
        marker.CustomTypeName = choice.CustomTypeName;
    }

    private void OpenSelectedMedia()=>_=OpenMarkerImageViewerAsync();

    private async Task SaveExternalToProjectAsync(HikingProject project,
        ExternalMediaReference reference, ExternalMediaDownloadResult downloaded)
    {
        var prepared = await Task.Run(() => _projectMedia.PrepareSaveExternalAsync(project, reference, downloaded));
        if (!ReferenceEquals(CurrentProject, project) || !project.ExternalMediaReferences.Contains(reference))
            throw new InvalidOperationException("项目已切换，图片没有保存到其它项目。");
        if (project.MediaAttachments.Any(item => item.OwnerId == reference.OwnerId &&
            item.SourceKind == MediaSourceKind.ExternalUrl && item.SourceReference == reference.Url)) return;
        _history.Execute("保存网络图片到项目", () => project.ExecuteAtomicUpdate(p =>
        {
            if (prepared.NewBlob is not null) p.MediaBlobs.Add(prepared.NewBlob);
            p.MediaAttachments.Add(prepared.Attachment);
        }), () => project.ExecuteAtomicUpdate(p =>
        {
            p.MediaAttachments.Remove(prepared.Attachment);
            if (prepared.NewBlob is not null) p.MediaBlobs.Remove(prepared.NewBlob);
        }));
        OnPropertyChanged(nameof(WindowTitle));
    }

    public Task SaveExternalForAutomationAsync(ExternalMediaReference reference,
        ExternalMediaDownloadResult downloaded) => CurrentProject is { } project
        ? SaveExternalToProjectAsync(project, reference, downloaded)
        : throw new InvalidOperationException("当前没有项目。");

    private static void SetMediaOwnerType(HikingProject project, Guid markerId, MediaOwnerType type)
    {
        foreach (var item in project.MediaAttachments.Where(item => item.OwnerId == markerId)) item.OwnerType = type;
        foreach (var item in project.ExternalMediaReferences.Where(item => item.OwnerId == markerId)) item.OwnerType = type;
        project.MarkDirty();
    }

    private void DeleteMarker(MapMarkerBase? marker)
    {
        var project = CurrentProject;
        if (project is null || marker is null) return;
        if (MessageBox.Show(System.Windows.Application.Current.MainWindow,
            $"确定删除“{marker.Name ?? "未命名标注"}”吗？此操作可以撤销。",
            "确认删除标注", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var markerMedia = project.MediaAttachments.Where(item => item.OwnerId == marker.Id).ToArray();
        var markerExternal = project.ExternalMediaReferences.Where(item => item.OwnerId == marker.Id).ToArray();
        var markerBlobs = project.MediaBlobs.Where(blob =>
            markerMedia.Any(item => item.MediaBlobId == blob.Id) &&
            !project.MediaAttachments.Any(item => item.MediaBlobId == blob.Id && item.OwnerId != marker.Id)).ToArray();
        if (marker is Waypoint waypoint)
            _history.Execute("删除普通标注", () => project.ExecuteAtomicUpdate(p =>
            {
                p.Waypoints.Remove(waypoint);
                foreach (var item in markerMedia) p.MediaAttachments.Remove(item);
                foreach (var item in markerExternal) p.ExternalMediaReferences.Remove(item);
                foreach (var blob in markerBlobs) p.MediaBlobs.Remove(blob);
            }), () => project.ExecuteAtomicUpdate(p =>
            {
                p.Waypoints.Add(waypoint);
                foreach (var item in markerMedia) p.MediaAttachments.Add(item);
                foreach (var item in markerExternal) p.ExternalMediaReferences.Add(item);
                foreach (var blob in markerBlobs) p.MediaBlobs.Add(blob);
            }));
        else if (marker is GuideNode node)
        {
            var oldSegments = project.GuideSegments.ToArray();
            GuideSegment[]? newSegments = null;
            _history.Execute("删除攻略节点", () => project.ExecuteAtomicUpdate(p =>
            {
                p.GuideNodes.Remove(node);
                foreach (var item in markerMedia) p.MediaAttachments.Remove(item);
                foreach (var item in markerExternal) p.ExternalMediaReferences.Remove(item);
                foreach (var blob in markerBlobs) p.MediaBlobs.Remove(blob);
                newSegments ??= _guideStructure.RebuildSegments(p).ToArray();
                ReplaceGuideSegments(p, newSegments);
            }), () => project.ExecuteAtomicUpdate(p =>
            {
                p.GuideNodes.Add(node);
                foreach (var item in markerMedia) p.MediaAttachments.Add(item);
                foreach (var item in markerExternal) p.ExternalMediaReferences.Add(item);
                foreach (var blob in markerBlobs) p.MediaBlobs.Add(blob);
                ReplaceGuideSegments(p, oldSegments);
            }));
        }
        RefreshProjectView();
    }
    private static void ReplaceGuideSegments(HikingProject project, IEnumerable<GuideSegment> segments)
    {
        GuideStructureService.ReplaceSegments(project, segments);
    }

    private async Task RefreshRecentAsync()
    {
        var entries = await _recent.GetAsync();
        RecentProjects.Clear();
        foreach (var entry in entries) RecentProjects.Add(entry);
    }

    private void StartPeriodicAutoSave()
    {
        if (_applicationClosePending || IsBusy || !_periodicAutoSaveTask.IsCompleted) return;
        _periodicAutoSaveCancellation?.Dispose();
        _periodicAutoSaveCancellation = new CancellationTokenSource();
        var token = _periodicAutoSaveCancellation.Token;
        var contexts = _projects.OpenProjects.ToArray();
        _periodicAutoSaveTask = Task.Run(async () =>
        {
            try { foreach (var context in contexts) await _autoSave.SaveIfDirtyAsync(context.Project, context.FilePath, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { _log.Error(exception, "项目自动保存失败"); _uiContext?.Post(_ => StatusText = "自动保存失败，请检查磁盘空间。", null); }
        });
    }
    internal void ResumeAfterCancelledApplicationClose()
    {
        if (!_applicationClosePending) return;
        _applicationClosePending = false;
        _autoSaveTimer?.Change(_autoSave.Interval, _autoSave.Interval);
        foreach (var context in _projects.OpenProjects.Where(c => c.Project.IsDirty)) QueueGuideAutoSave(context.Project);
    }
    internal void StartPeriodicAutoSaveForAutomation() => StartPeriodicAutoSave();

    private Task RunProjectOperationAsync(string message, Func<IProgress<ProjectOperationProgress>, CancellationToken, Task> operation) =>
        RunOperationAsync(message, token => operation(new Progress<ProjectOperationProgress>(p => ProgressMessage = p.Message), token));
    private Task RunTrackOperationAsync(string message, Func<IProgress<TrackOperationProgress>, CancellationToken, Task> operation) =>
        RunOperationAsync(message, token => operation(new Progress<TrackOperationProgress>(p => ProgressMessage = p.Message), token));

    private async Task RunOperationAsync(string message, Func<CancellationToken, Task> operation)
    {
        _operationCancellation = new CancellationTokenSource();
        IsBusy = true; ProgressMessage = message; StatusText = message;
        try { await operation(_operationCancellation.Token); }
        catch (OperationCanceledException) { StatusText = "操作已取消"; }
        catch (ProjectVersionException exception) { _dialogs.ShowError("无法打开项目", exception.Message); StatusText = "打开项目失败"; }
        catch (ProjectFileException exception) { _log.Error(exception, "项目文件操作失败"); _dialogs.ShowError("项目文件操作失败", exception.Message); StatusText = "项目文件操作失败"; }
        catch (TrackExportException exception) { _log.Error(exception, "轨迹导出失败"); _dialogs.ShowError("导出失败", exception.Message); }
        catch (Exception exception) { _log.Error(exception, "操作发生未预期错误"); _dialogs.ShowError("操作失败", "操作过程中发生未预期错误，详细信息已经写入日志。"); }
        finally { _operationCancellation.Dispose(); _operationCancellation = null; ProgressMessage = ""; IsBusy = false; OnPropertyChanged(nameof(WindowTitle)); }
    }

    private bool CanSave() => !IsBusy && CurrentProject is not null;
    internal Dictionary<string, double> LifecycleTimings { get; } = [];
    private bool CanExport() => !IsBusy && CurrentProject is { Tracks.Count: > 0 };
    private void RaiseCommandStates()
    {
        RaiseKilometerCommands();
        RaiseRoutingCommandStates();
        _enterTrackEditCommand.RaiseCanExecuteChanged();
        _exitTrackEditCommand.RaiseCanExecuteChanged();
        _enterTrackEditContextCommand.RaiseCanExecuteChanged();
        _exitTrackEditContextCommand.RaiseCanExecuteChanged();
        _deleteTrackPointCommand.RaiseCanExecuteChanged();
        _addTrackPointCommand.RaiseCanExecuteChanged();
        _splitTrackSegmentCommand.RaiseCanExecuteChanged();
        _selectTrackRangeCommand.RaiseCanExecuteChanged();
        _deleteTrackRangeCommand.RaiseCanExecuteChanged();
        _cropTrackRangeCommand.RaiseCanExecuteChanged();
        _reverseTrackCommand.RaiseCanExecuteChanged();
        _deleteTrackSegmentCommand.RaiseCanExecuteChanged();
        _reverseTrackSegmentCommand.RaiseCanExecuteChanged();
        _mergeAdjacentSegmentsCommand.RaiseCanExecuteChanged();
        _connectAdjacentSegmentsCommand.RaiseCanExecuteChanged();
        _createMergedTrackCommand.RaiseCanExecuteChanged();
        _createConnectedTrackCommand.RaiseCanExecuteChanged();
        _newCommand.RaiseCanExecuteChanged(); _openCommand.RaiseCanExecuteChanged(); _saveCommand.RaiseCanExecuteChanged();
        _saveAsCommand.RaiseCanExecuteChanged(); _closeCommand.RaiseCanExecuteChanged(); _importGpxCommand.RaiseCanExecuteChanged();
        _closeProjectContextCommand.RaiseCanExecuteChanged();
        _importKmlCommand.RaiseCanExecuteChanged(); _importKmzCommand.RaiseCanExecuteChanged(); _exportGpxCommand.RaiseCanExecuteChanged(); _exportKmlCommand.RaiseCanExecuteChanged(); _exportKmzCommand.RaiseCanExecuteChanged(); _mergedExportCommand.RaiseCanExecuteChanged();
        _resnapActiveGuidesCommand.RaiseCanExecuteChanged();
        _setActiveCommand.RaiseCanExecuteChanged(); _cancelCommand.RaiseCanExecuteChanged();
        _exportTrackGpxCommand.RaiseCanExecuteChanged(); _exportTrackKmlCommand.RaiseCanExecuteChanged();
        _exportTrackKmzCommand.RaiseCanExecuteChanged();
        _viewTrackPropertiesCommand.RaiseCanExecuteChanged();
        _editProjectGeometryItemCommand.RaiseCanExecuteChanged();
        _editMarkerCommand.RaiseCanExecuteChanged();
        _deleteMarkerCommand.RaiseCanExecuteChanged();
        _pickGuidePositionCommand.RaiseCanExecuteChanged();
        RaiseMarkerMenuStates();
    }

    private void RaiseMarkerMenuStates()
    {
        _addWaypointMenuCommand.RaiseCanExecuteChanged();
        _addGuideMenuCommand.RaiseCanExecuteChanged();
        _editSelectedMenuCommand.RaiseCanExecuteChanged();
        _deleteSelectedMenuCommand.RaiseCanExecuteChanged();
        _convertToGuideMenuCommand.RaiseCanExecuteChanged();
        _convertToWaypointMenuCommand.RaiseCanExecuteChanged();
        _resnapSelectedMenuCommand.RaiseCanExecuteChanged();
        _pickSelectedMenuCommand.RaiseCanExecuteChanged();
        _associateCurrentTrackMenuCommand.RaiseCanExecuteChanged();
        _checkGuidesMenuCommand.RaiseCanExecuteChanged();
        _openMediaCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _markerThumbnailLifetime.Cancel();
        foreach (var token in _guideSaveDebounce.Values.ToArray()) token.Cancel();
        _periodicAutoSaveCancellation?.Cancel();
        _periodicAutoSaveCancellation?.Dispose();
        if (_observedPrimaryTrack is not null) _observedPrimaryTrack.PropertyChanged -= OnPrimaryTrackChanged;
        _routeRequest?.Cancel();
        _routeLifetime.Cancel();
        _routeLifetime.Dispose();
        _autoSaveTimer?.Dispose();
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        Map.Dispose();
    }

    private enum TrackClickMode { SelectPoint, InsertPoint, SplitSegment, SelectRange }
}
