using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using HikingGuide.Application.Logging;
using HikingGuide.Application.Guides;
using HikingGuide.Application.Maps;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.App.ViewModels;

public sealed partial class MapViewModel : ViewModelBase, IDisposable
{
    private readonly IGlobalSettingsService _settings;
    private readonly MapDisplayDataService _displayData;
    private readonly MapGeoJsonBuilder _geoJson;
    private readonly DirectionArrowCache _arrowCache = new();
    private Guid[] _selectedSplitSectionIds = [];
    private Guid[] _selectedTrackIds = [];
    private readonly MapDisplayProfile _profile;
    private readonly ILoggingService _log;
    private readonly MapRevisionGate _revisionGate = new();
    private IMapBridge? _bridge;
    private IReadOnlyList<HikingGuide.Core.Favorites.FavoritePoint> _favorites = [];
    private bool _favoritesVisible = true;
    private long _favoritesRevision;
    public bool IsFavoriteDragging { get; private set; }
    private HikingGuide.Core.Favorites.FavoriteSelectionService? _favoriteSelection;
    private long _favoriteSelectionRevision;
    public void ConfigureFavoriteSelection(HikingGuide.Core.Favorites.FavoriteSelectionService selection)
    {
        _favoriteSelection = selection;
        selection.SelectionChanged += (_, _) => _ = SendFavoriteCommandAsync("SetSelectedFavorite", new { favoriteId = selection.SelectedFavoritePointId, selectionRevision = ++_favoriteSelectionRevision });
    }
    public Task SendFavoriteCommandAsync(string type, object? payload = null) => _bridge?.IsReady == true
        ? _bridge.SendAsync(MapProtocol.SerializeCommand(type, payload, revision: _favoritesRevision)) : Task.CompletedTask;
    public async Task ShowFavoriteHoverAsync(MapEventMessage request, object card, byte[]? thumbnail)
    {
        if (request.Revision != _favoritesRevision || _bridge?.IsReady != true) return;
        var url = thumbnail is not null && _bridge is IMediaMapBridge media ? media.RegisterMediaThumbnail(thumbnail) : null;
        var values = System.Text.Json.JsonSerializer.SerializeToElement(card, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var payload = values.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        payload["thumbnailUrl"] = url; payload["requestId"] = request.RequestId; payload["favoriteId"] = request.FavoriteId;
        await SendFavoriteCommandAsync("ShowFavoriteHover", payload);
    }
    public event EventHandler<MapEventMessage>? FavoritesEvent;
    public async Task UpdateFavoritesAsync(IReadOnlyList<HikingGuide.Core.Favorites.FavoritePoint> points, bool visible)
    {
        _favorites = points; _favoritesVisible = visible;
        if (_bridge?.IsReady != true) return;
        var expected = ++_favoritesRevision;
        var data = await Task.Run(() => new { type = "FeatureCollection", features = points.Select(p => new {
            type = "Feature", properties = new { favoriteId = p.Id, name = p.Name },
            geometry = new { type = "Point", coordinates = new[] { p.Longitude, p.Latitude } } }).ToArray() });
        if (expected != _favoritesRevision) return;
        await _bridge.SendAsync(MapProtocol.SerializeCommand("SetFavorites", new { geoJson = data, visible,
            style = new HikingGuide.Core.Favorites.FavoriteMapStyleProfile(), selectedFavoriteId = _favoriteSelection?.SelectedFavoritePointId,
            selectionRevision = _favoriteSelectionRevision,
            dragThresholdX = System.Windows.SystemParameters.MinimumHorizontalDragDistance,
            dragThresholdY = System.Windows.SystemParameters.MinimumVerticalDragDistance }, revision: expected));
    }
    public Task LocateFavoriteAsync(HikingGuide.Core.Favorites.FavoritePoint point) => _bridge?.IsReady == true
        ? _bridge.SendAsync(MapProtocol.SerializeCommand("FlyToFavorite", new { favoriteId = point.Id,
            latitude = point.Latitude, longitude = point.Longitude,
            minimumZoom = new HikingGuide.Core.Favorites.FavoriteMapStyleProfile().MinimumLocateZoom }, revision: _favoritesRevision)) : Task.CompletedTask;
    public Task RunFavoritesSmokeAsync() => _bridge?.IsReady == true ?
        _bridge.SendAsync(MapProtocol.SerializeCommand("RunFavoritesSmoke", revision: _favoritesRevision)) : Task.CompletedTask;
    private HikingProject? _project;
    private CancellationTokenSource? _renderCancellation;
    private CancellationTokenSource? _viewCancellation;
    private long _revision;
    private long _projectContextVersion;
    private long _trackEditContextVersion;
    private Track? _editingTrack;
    private Guid? _editingSessionId;
    private long _editingGeometryRevision;

    public Task SetElevationHoverAsync(GeoCoordinate? coordinate)
    {
        if (_bridge?.IsReady != true || _project is null) return Task.CompletedTask;
        return _bridge.SendAsync(MapProtocol.SerializeCommand("SetElevationHover",
            coordinate is null ? new { latitude = (double?)null, longitude = (double?)null } :
                new { latitude = (double?)coordinate.Value.Latitude,
                    longitude = (double?)coordinate.Value.Longitude },
            _project.Id, _revision));
    }
    private long _focusRequestVersion;
    private int _lastZoomBucket = -1;
    private MapDisplayData? _lastDisplayData;
    private MapSourceDefinition? _selectedMapSource;
    private long _mapSettingsRevision;
    private string? _mapStackSignature;
    private MapEventMessage? _lastView;
    private EventHandler? _onMapSettingsChanged;
    private MapSelection _selection = MapSelection.None;
    private Guid? _temporaryVisibleTrackId;
    private string _mapStatusText = "地图正在初始化……";
    private string _mouseCoordinateText = "经纬度：—";
    private string _zoomText = "缩放：—";
    private bool _isReady;
    private DateTimeOffset _lastTileErrorLog = DateTimeOffset.MinValue;

    public MapViewModel(
        IGlobalSettingsService settings,
        MapDisplayDataService displayData,
        MapGeoJsonBuilder geoJson,
        MapDisplayProfile profile,
        ILoggingService log)
    {
        _settings = settings;
        _displayData = displayData;
        _geoJson = geoJson;
        _profile = profile;
        _log = log;
        ReloadCommand = new AsyncRelayCommand(ReloadAsync);
        FitCurrentTrackCommand = new AsyncRelayCommand(FitCurrentTrackAsync, () => _project?.ActiveTrack is not null && IsReady);
        FitAllCommand = new AsyncRelayCommand(FitAllAsync, () => _project is not null && IsReady);
        ZoomInCommand = new AsyncRelayCommand(() => ChangeZoomAsync(1), () => IsReady);
        ZoomOutCommand = new AsyncRelayCommand(() => ChangeZoomAsync(-1), () => IsReady);
        SelectMapSourceCommand = new RelayCommand<MapSourceDefinition>(source =>
        {
            if (source is not null) SelectedMapSource = source;
        });
        RefreshMapSources();
        _onMapSettingsChanged = (_, _) => { RefreshMapSources(); NotifyQuickLayers(); _ = ApplyGlobalMapLayersAsync(); };
        _settings.MapSettingsChanged += _onMapSettingsChanged;
    }

    public event EventHandler<MapSelection>? SelectionRequested;
    public event EventHandler<MapEventMessage>? SplitSectionSelectionRequested;
    public event EventHandler<MapEventMessage>? RightClicked;
    public event EventHandler<MapEventMessage>? MediaOpenRequested;
    public event EventHandler<MapEventMessage>? MediaThumbnailRequested;
    public event EventHandler<MapEventMessage>? MediaThumbnailReady;
    public event EventHandler<MapMarkerEditRequest>? MarkerEditRequested;
    public event EventHandler<MapEventMessage>? TrackEditRequested;
    public event EventHandler<MapEventMessage>? RoutePlanningPointPicked;
    public event EventHandler<MapSnapLocationPick>? SnapLocationPicked;
    public event EventHandler<string>? UserMessage;
    public event EventHandler<double>? RenderCompleted;
    public event EventHandler<string>? InteractionSmokeCompleted;

    public ObservableCollection<MapSourceDefinition> MapSources { get; } = [];
    public MapSourceDefinition? SelectedMapSource
    {
        get => _selectedMapSource;
        set
        {
            if (value is null || _selectedMapSource?.Id == value.Id) return;
            _ = SelectMapSourceAsync(value);
        }
    }
    public Guid? SelectedMapSourceId
    {
        get=>_selectedMapSource?.Id;
        set{if(value is Guid id&&MapSources.FirstOrDefault(s=>s.Id==id) is {} source)SelectedMapSource=source;}
    }
    public bool IsReady
    {
        get => _isReady;
        private set
        {
            if (!SetField(ref _isReady, value)) return;
            RaiseCommandStates();
        }
    }
    public string MapStatusText { get => _mapStatusText; private set => SetField(ref _mapStatusText, value); }
    internal string? LastTileErrorMessage { get; private set; }
    public string MouseCoordinateText { get => _mouseCoordinateText; private set => SetField(ref _mouseCoordinateText, value); }
    public string ZoomText { get => _zoomText; private set => SetField(ref _zoomText, value); }
    public bool IsTrackEditing => _editingTrack is not null && _editingSessionId.HasValue;
    public string TrackEditingHint => IsTrackEditing
        ? $"编辑：{_editingTrack!.Name ?? "未命名轨迹"}；可点选其他轨迹，Esc取消预览"
        : "普通地图模式";
    public ICommand ReloadCommand { get; }
    public ICommand FitCurrentTrackCommand { get; }
    public ICommand FitAllCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand SelectMapSourceCommand { get; }
    public int LastOriginalPointCount => _lastDisplayData?.Tracks.Sum(track => track.OriginalPointCount) ?? 0;
    public int LastDisplayPointCount => _lastDisplayData?.Tracks.Sum(track => track.DisplayPointCount) ?? 0;
    public double? LastMapLibreReadyMilliseconds { get; private set; }

    public async Task AttachBridgeAsync(IMapBridge bridge, CancellationToken cancellationToken = default)
    {
        if (_bridge is not null)
        {
            _bridge.EventReceived -= OnMapEvent;
            _bridge.InitializationFailed -= OnInitializationFailed;
        }
        _bridge = bridge;
        bridge.EventReceived += OnMapEvent;
        bridge.InitializationFailed += OnInitializationFailed;
        MapStatusText = "地图正在初始化……";
        await bridge.InitializeAsync(cancellationToken);
    }

    public async Task SetProjectAsync(HikingProject? project)
    {
        _renderCancellation?.Cancel();
        _viewCancellation?.Cancel();
        Interlocked.Increment(ref _focusRequestVersion);
        var bridge = _bridge;
        var previousProject = _project;
        var previousRevision = _revision;
        var switched = !ReferenceEquals(previousProject, project);
        var contextVersion = ++_projectContextVersion;
        // 必须在第一次 await 前绑定项目与版本，旧 SetProject 不能稍后覆盖较新请求。
        _project = project;
        _revision = _revisionGate.Begin(project?.Id);
        var expectedRevision = _revision;
        ResetTrackEditContext();
        if (switched)
        {
            _arrowCache.Clear();
            _selectedSplitSectionIds = [];
            _selectedTrackIds = [];
        }
        if (_bridge is IMediaMapBridge mediaBridge) mediaBridge.ClearMediaThumbnails();
        _temporaryVisibleTrackId = null;
        _selection = MapSelection.None;
        _lastDisplayData = null;
        _lastZoomBucket = -1;
        if (switched && bridge?.IsReady == true && previousProject is not null)
            await bridge.SendAsync(MapProtocol.SerializeCommand("EndRoutePlanning", null,
                previousProject.Id, previousRevision));
        if (!IsProjectContextCurrent(project, expectedRevision, contextVersion)) return;
        if (project is null)
        {
            if (bridge?.IsReady == true) await bridge.SendAsync(MapProtocol.SerializeCommand("ClearProject"));
            if (!IsProjectContextCurrent(project, expectedRevision, contextVersion)) return;
            await ApplyGlobalMapLayersAsync();
            if (!IsProjectContextCurrent(project, expectedRevision, contextVersion)) return;
            MapStatusText = "未打开项目";
            RaiseCommandStates();
            return;
        }

        if (bridge?.IsReady == true) await SynchronizeAllAsync(restoreView: switched);
        if (!IsProjectContextCurrent(project, expectedRevision, contextVersion)) return;
        RaiseCommandStates();
    }

    private bool IsProjectContextCurrent(HikingProject? project, long revision, long version) =>
        ReferenceEquals(_project, project) && _revision == revision && _projectContextVersion == version;

    private void ResetTrackEditContext()
    {
        ++_trackEditContextVersion;
        _editingTrack = null;
        _editingSessionId = null;
        _editingGeometryRevision = 0;
        NotifyTrackEditContext();
    }

    private void NotifyTrackEditContext()
    {
        OnPropertyChanged(nameof(IsTrackEditing));
        OnPropertyChanged(nameof(TrackEditingHint));
    }

    public void InvalidateTrack(Guid trackId)
    {
        _displayData.InvalidateTrack(trackId);
        _arrowCache.Invalidate(trackId);
    }

    public async Task SetSplitSectionSelectionAsync(IEnumerable<Guid> ids)
    {
        _selectedSplitSectionIds = ids.Distinct().ToArray();
        if (_bridge?.IsReady == true && _project is { } project)
            await _bridge.SendAsync(MapProtocol.SerializeCommand("SetSplitSectionSelection",
                new { ids = _selectedSplitSectionIds, trackIds = _selectedTrackIds }, project.Id, _revision));
    }

    public Task SetGeometrySelectionAsync(IEnumerable<Guid> trackIds, IEnumerable<Guid> sectionIds)
    {
        _selectedTrackIds = trackIds.Distinct().ToArray();
        return SetSplitSectionSelectionAsync(sectionIds);
    }

    public async Task BeginTrackEditAsync(Track track, Guid editSessionId)
    {
        var project = _project;
        var bridge = _bridge;
        if (bridge?.IsReady != true || project?.Tracks.Contains(track) != true) return;
        var revision = _revision;
        var geometryRevision = track.GeometryRevision;
        var contextVersion = ++_trackEditContextVersion;
        var previousTemporaryTrackId = _temporaryVisibleTrackId;
        _editingTrack = track;
        _editingSessionId = editSessionId;
        _editingGeometryRevision = geometryRevision;
        _temporaryVisibleTrackId = track.Id;
        NotifyTrackEditContext();
        // 可见轨迹间换目标无需重算或重发全部 GeoJSON，只有临时显隐真正改变时刷新。
        var requiresVisibilityRefresh = previousTemporaryTrackId != track.Id &&
            !MapLayerVisibility.IsTrackVisible(project.Settings.Map, track.Id) ||
            previousTemporaryTrackId is Guid previousId && previousId != track.Id &&
            !MapLayerVisibility.IsTrackVisible(project.Settings.Map, previousId);
        if (requiresVisibilityRefresh) await RefreshVisibilityAsync();
        if (!IsTrackEditScopeCurrent(project, revision, track, geometryRevision, editSessionId, contextVersion)) return;
        await bridge.SendAsync(MapProtocol.SerializeCommand("BeginTrackEdit", new
        { trackId = track.Id, geometryRevision, editSessionId, editContextVersion = contextVersion }, project.Id, revision));
    }

    public Task UpdateTrackEditContextAsync(Track track, Guid editSessionId) =>
        ReferenceEquals(_editingTrack, track) && _project?.Tracks.Contains(track) == true
            ? BeginTrackEditAsync(track, editSessionId) : Task.CompletedTask;

    public async Task EndTrackEditAsync()
    {
        var project = _project;
        var revision = _revision;
        var contextVersion = ++_trackEditContextVersion;
        var previousTemporaryTrackId = _temporaryVisibleTrackId;
        _editingTrack = null;
        _editingSessionId = null;
        _editingGeometryRevision = 0;
        _temporaryVisibleTrackId = null;
        NotifyTrackEditContext();
        var bridge = _bridge;
        if (bridge?.IsReady != true) return;
        await bridge.SendAsync(MapProtocol.SerializeCommand("EndTrackEdit", new
        { editContextVersion = contextVersion }, project?.Id, revision));
        if (!ReferenceEquals(project, _project) || revision != _revision ||
            contextVersion != _trackEditContextVersion) return;
        if (project is not null && previousTemporaryTrackId is Guid previousId &&
            !MapLayerVisibility.IsTrackVisible(project.Settings.Map, previousId))
            await RefreshVisibilityAsync();
    }

    private bool IsTrackEditScopeCurrent(HikingProject project, long revision, Track track,
        long geometryRevision, Guid editSessionId, long version) =>
        ReferenceEquals(_project, project) && _revision == revision &&
        version == _trackEditContextVersion && ReferenceEquals(_editingTrack, track) &&
        _editingSessionId == editSessionId && _editingGeometryRevision == geometryRevision &&
        track.GeometryRevision == geometryRevision && project.Tracks.Contains(track);

    public bool IsTrackMapContextCurrent(MapEventMessage value) =>
        value.ProjectId is Guid projectId && _project?.Id == projectId && _revisionGate.IsCurrent(projectId, value.Revision) &&
        _project.Tracks.FirstOrDefault(track => track.Id == value.TrackId) is { } track &&
        track.GeometryRevision == value.GeometryRevision &&
        (value.EditSessionId is null ? !IsTrackEditing :
            _editingSessionId == value.EditSessionId && ReferenceEquals(_editingTrack, track));

    public Task BeginRoutePlanningAsync() =>
        _bridge?.IsReady == true && _project is not null
            ? _bridge.SendAsync(MapProtocol.SerializeCommand("BeginRoutePlanning", null,
                _project.Id, _revision)) : Task.CompletedTask;

    public Task RunP7PlanningClickSmokeAsync(double latitude, double longitude) =>
        _bridge?.IsReady == true && _project is not null
            ? _bridge.SendAsync(MapProtocol.SerializeCommand("RunP7PlanningClickSmoke",
                new { latitude, longitude }, _project.Id, _revision)) : Task.CompletedTask;

    public Task EndRoutePlanningAsync() =>
        _bridge?.IsReady == true && _project is not null
            ? _bridge.SendAsync(MapProtocol.SerializeCommand("EndRoutePlanning", null,
                _project.Id, _revision)) : Task.CompletedTask;

    public Task SetRoutePreviewAsync(RoutePlan? plan)
    {
        if (_bridge?.IsReady != true || _project is null) return Task.CompletedTask;
        var legs = new { type = "FeatureCollection", features = plan?.Legs.Select(leg => new
        {
            type = "Feature", properties = new { mode = leg.Mode.ToString() },
            geometry = new { type = "LineString",
                coordinates = leg.Geometry.Select(point => new[] { point.Longitude, point.Latitude }).ToArray() }
        }).ToArray() ?? [] };
        var points = new { type = "FeatureCollection", features = plan?.ControlPoints.Select((point, index) => new
        {
            type = "Feature", properties = new { role = point.Type.ToString(),
                label = $"{index + 1} {(point.Type == RouteControlPointType.Start ? "起点" :
                    point.Type == RouteControlPointType.End ? "终点" : "途经点")}" },
            geometry = new { type = "Point",
                coordinates = new[] { point.Coordinate.Longitude, point.Coordinate.Latitude } }
        }).ToArray() ?? [] };
        return _bridge.SendAsync(MapProtocol.SerializeCommand("SetRoutePreview",
            new { legs, points }, _project.Id, _revision));
    }

    public Task SetTrackEditHandlesAsync(Track track, Guid editSessionId,
        IReadOnlyList<TrackEditHandle> handles) => SendTrackEditCommandAsync("SetTrackEditHandles", track, editSessionId, new
        {
            trackId = track.Id, geometryRevision = track.GeometryRevision, editSessionId,
            editContextVersion = _trackEditContextVersion,
            handles = handles.Select(item => new { pointId = item.PointId,
                longitude = item.Coordinate.Longitude, latitude = item.Coordinate.Latitude,
                selected = item.Selected }).ToArray()
        });

    public Task PreviewTrackRangeAsync(Track track, Guid editSessionId,
        TrackRangePreview preview) => SendTrackEditCommandAsync("PreviewTrackRange", track, editSessionId, new
        {
            trackId = track.Id, geometryRevision = track.GeometryRevision, editSessionId,
            editContextVersion = _trackEditContextVersion,
            geoJson = new { type = "FeatureCollection", features = new[]
            {
                new { type = "Feature", geometry = new { type = "MultiLineString",
                    coordinates = preview.DisplayPieces.Select(piece => piece.Select(point =>
                        new[] { point.Longitude, point.Latitude }).ToArray()).ToArray() } }
            } }
        });

    public Task ClearTrackRangePreviewAsync(Track track, Guid editSessionId) =>
        SendTrackEditCommandAsync("PreviewTrackRange", track, editSessionId, new
            {
                trackId = track.Id, geometryRevision = track.GeometryRevision, editSessionId,
                editContextVersion = _trackEditContextVersion,
                geoJson = new { type = "FeatureCollection", features = Array.Empty<object>() }
            });

    private Task SendTrackEditCommandAsync(string type, Track track, Guid editSessionId, object payload)
    {
        var project = _project;
        var bridge = _bridge;
        var revision = _revision;
        return project is not null && bridge?.IsReady == true &&
            IsTrackEditScopeCurrent(project, revision, track, track.GeometryRevision,
                editSessionId, _trackEditContextVersion)
            ? bridge.SendAsync(MapProtocol.SerializeCommand(type, payload, project.Id, revision))
            : Task.CompletedTask;
    }

    public Task RunP6TrackEditSmokeAsync(Guid pointId, GeoCoordinate click,
        GeoCoordinate moveTo) => _bridge?.IsReady == true
        ? _bridge.SendAsync(MapProtocol.SerializeCommand("RunP6TrackEditSmoke", new
        { pointId, clickLatitude = click.Latitude, clickLongitude = click.Longitude,
            newLatitude = moveTo.Latitude, newLongitude = moveTo.Longitude }, _project?.Id, _revision))
        : Task.CompletedTask;

    public Task RunP61SplitSectionSmokeAsync(Guid sectionId, bool control = false,
        bool contextMenu = false) => _bridge?.IsReady == true
        ? _bridge.SendAsync(MapProtocol.SerializeCommand("RunP61SplitSectionSmoke",
            new { splitSectionId = sectionId, ctrlKey = control, contextMenu },
            _project?.Id, _revision)) : Task.CompletedTask;

    public Task RefreshEditedTrackAsync(Track track) => _project?.Tracks.Contains(track) == true
        ? RefreshEditedTrackCoreAsync(track) : Task.CompletedTask;

    private async Task RefreshEditedTrackCoreAsync(Track track)
    {
        InvalidateTrack(track.Id);
        if (_project is not null) await RebuildDisplayDataAsync(GetProjectZoom(_project), _revision);
    }

    public async Task SetMarkerPopupThumbnailAsync(Guid projectId, long expectedRevision, Guid markerId, byte[] pngBytes)
    {
        if (_project?.Id != projectId || _revision != expectedRevision ||
            _bridge is not IMediaMapBridge media || _bridge.IsReady != true) return;
        var revision = _revision;
        var url = media.RegisterMediaThumbnail(pngBytes);
        await _bridge.SendAsync(MapProtocol.SerializeCommand("SetMarkerPopupMedia",
            new { markerId, thumbnailUrl = url }, projectId, revision));
    }

    public Task RunP51MediaSmokeAsync() => _bridge?.SendAsync(
        MapProtocol.SerializeCommand("RunP51MediaSmoke", projectId: _project?.Id, revision: _revision))
        ?? Task.CompletedTask;

    public Task SetMarkerPopupErrorAsync(Guid projectId, long expectedRevision, Guid markerId, string message) =>
        _project?.Id == projectId && _revision == expectedRevision && _bridge?.IsReady == true
            ? _bridge.SendAsync(MapProtocol.SerializeCommand("SetMarkerPopupError",
                new { markerId, message }, projectId, expectedRevision))
            : Task.CompletedTask;

    public void RefreshMapSources()
    {
        var stack = MapLayerStackService.Normalize(_settings.Current.MapLayerStack,_settings.Current.MapSources);
        _settings.Current.MapLayerStack = stack;
        var available=_settings.Current.MapSources.Where(source=>MapLayerStackService.IsAvailable(source,MapSourceUsage.Base)).ToArray();
        // 不清空绑定集合；清空会让选择框先失去选择，随后WPF可能把迟到的空值写回界面。
        for(var i=0;i<available.Length;i++)
        {
            var source=available[i];var existing=MapSources.FirstOrDefault(s=>s.Id==source.Id);
            if(existing is null)MapSources.Insert(i,source);
            else{var index=MapSources.IndexOf(existing);if(index!=i)MapSources.Move(index,i);if(!ReferenceEquals(MapSources[i],source))MapSources[i]=source;}
        }
        while(MapSources.Count>available.Length)MapSources.RemoveAt(MapSources.Count-1);
        var selected = MapSources.FirstOrDefault(source => source.Id == stack.BaseSourceId);
        SelectSourceWithoutCallback(selected);
    }

    public async Task NotifyProjectDataChangedAsync() => await SetProjectAsync(_project);

    public Task RefreshVisibilityAsync() => _project is null ? Task.CompletedTask :
        RebuildDisplayDataAsync(GetProjectZoom(_project), _revision);

    public async Task SelectObjectAsync(object? model, bool focusOnMap = false,
        bool cancelPendingFocus = true)
    {
        if (_bridge?.IsReady != true) return;
        var focusVersion = cancelPendingFocus
            ? Interlocked.Increment(ref _focusRequestVersion) : Interlocked.Read(ref _focusRequestVersion);
        var project = _project;
        var revision = _revision;
        _selection = model switch
        {
            Track track => new MapSelection(MapSelectionKind.Track, track.Id),
            Waypoint waypoint => new MapSelection(MapSelectionKind.Waypoint, waypoint.Id),
            GuideNode guideNode => new MapSelection(MapSelectionKind.GuideNode, guideNode.Id),
            GuideSegment guideSegment => new MapSelection(MapSelectionKind.GuideSegment, guideSegment.Id),
            _ => MapSelection.None
        };
        await _bridge.SendAsync(MapProtocol.SerializeCommand("SetSelection", new
        {
            kind = _selection.Kind.ToString().ToLowerInvariant(),
            objectId = _selection.ObjectId
        }, project?.Id, revision));
        if (focusOnMap && ReferenceEquals(project, _project) && revision == _revision &&
            focusVersion == Interlocked.Read(ref _focusRequestVersion))
            await FocusObjectAsync(model);
    }

    public async Task FocusObjectAsync(object? model)
    {
        FocusObjectRequestCount++;
        var focusVersion = Interlocked.Increment(ref _focusRequestVersion);
        var project = _project;
        var revision = _revision;
        if (_bridge?.IsReady != true || project is null) return;
        GeoCoordinate? point = model switch
        {
            Waypoint waypoint => waypoint.Coordinate,
            GuideNode node => node.IsOrphaned ? node.OriginalCoordinate : node.SnappedCoordinate ?? node.OriginalCoordinate,
            _ => null
        };
        if (point is { } coordinate)
        {
            await _bridge.SendAsync(MapProtocol.SerializeCommand("FlyToWaypoint", new
            {
                longitude = coordinate.Longitude, latitude = coordinate.Latitude, minimumZoom = 16
            }, project.Id, _revision));
            return;
        }
        var bounds = await Task.Run(() => model switch
        {
            Track track => MapBounds.FromCoordinates(track.Segments.SelectMany(segment => segment.Points)
                .Select(value => new MapCoordinate(value.Longitude, value.Latitude))),
            TrackSegment segment => MapBounds.FromCoordinates(segment.Points
                .Select(value => new MapCoordinate(value.Longitude, value.Latitude))),
            SplitSection section => MapBounds.FromCoordinates(section.GeometryParts.SelectMany(part => part.Points)
                .Select(value => new MapCoordinate(value.Longitude, value.Latitude))),
            GuideSegment guideSegment => GetGuideSegmentBounds(project, guideSegment),
            _ => null
        });
        if (ReferenceEquals(project, _project) && revision == _revision &&
            focusVersion == Interlocked.Read(ref _focusRequestVersion))
            await SendBoundsAsync(bounds);
    }

    public int FocusObjectRequestCount { get; private set; }

    public Task RunP62TrackClickSmokeAsync(Guid trackId) => _bridge?.SendAsync(
        MapProtocol.SerializeCommand("RunP62TrackClickSmoke", new { trackId }, _project?.Id, _revision))
        ?? Task.CompletedTask;

    public Task RunP62RightClickSmokeAsync(Guid trackId, Guid splitSectionId,
        double latitude, double longitude) => _bridge?.SendAsync(
        MapProtocol.SerializeCommand("RunP62RightClickSmoke",
            new { trackId, splitSectionId, latitude, longitude }, _project?.Id, _revision))
        ?? Task.CompletedTask;

    public Task FocusCoordinateAsync(GeoCoordinate coordinate) =>
        _bridge?.IsReady == true && _project is { } project
            ? _bridge.SendAsync(MapProtocol.SerializeCommand("FlyToWaypoint", new
            { longitude = coordinate.Longitude, latitude = coordinate.Latitude, minimumZoom = 16 },
                project.Id, _revision))
            : Task.CompletedTask;

    public Task CenterElevationPointAsync(GeoCoordinate coordinate) =>
        _bridge?.IsReady == true && _project is { } project
            ? _bridge.SendAsync(MapProtocol.SerializeCommand("SetView", new
            { longitude = coordinate.Longitude, latitude = coordinate.Latitude }, project.Id, _revision))
            : Task.CompletedTask;

    private static MapBounds? GetGuideSegmentBounds(HikingProject project, GuideSegment segment)
    {
        var track = project.Tracks.FirstOrDefault(value => value.Id == segment.TrackId);
        var start = project.GuideNodes.FirstOrDefault(value => value.Id == segment.StartNodeId);
        var end = project.GuideNodes.FirstOrDefault(value => value.Id == segment.EndNodeId);
        if (track is null || start?.ExactPosition is null || end?.ExactPosition is null) return null;
        return MapBounds.FromCoordinates(new GuideStructureService().ExtractRange(track, start.ExactPosition, end.ExactPosition)
            .SelectMany(piece => piece).Select(value => new MapCoordinate(value.Longitude, value.Latitude)));
    }

    public void RequestAddWaypoint(double latitude, double longitude)
    {if(AllowTerrainCoordinateEdit())MarkerEditRequested?.Invoke(this, new MapMarkerEditRequest(MapMarkerEditKind.AddWaypoint, null, new GeoCoordinate(latitude, longitude)));}
    public void RequestAddGuideNode(double latitude, double longitude)
    {if(AllowTerrainCoordinateEdit())MarkerEditRequested?.Invoke(this, new MapMarkerEditRequest(MapMarkerEditKind.AddGuideNode, null, new GeoCoordinate(latitude, longitude)));}

    public Task BeginSnapLocationPickAsync(Guid guideNodeId) => _bridge?.SendAsync(
        MapProtocol.SerializeCommand("BeginSnapLocationPick", new { guideNodeId }, _project?.Id, _revision))
        ?? Task.CompletedTask;

    public Task BeginMarkerAddAsync(bool guideNode) => _bridge?.SendAsync(
        MapProtocol.SerializeCommand("BeginMarkerAdd", new { guideNode }, _project?.Id, _revision))
        ?? Task.CompletedTask;

    private async Task SynchronizeAllAsync(bool restoreView = true)
    {
        var project = _project;
        var bridge = _bridge;
        if (bridge?.IsReady != true) return;
        if (project is null) { await ApplyGlobalMapLayersAsync(true); return; }
        var expectedRevision = _revision;
        var contextVersion = _projectContextVersion;
        var editContextVersion = _trackEditContextVersion;
        await bridge.SendAsync(MapProtocol.SerializeCommand("Initialize", null, project.Id, expectedRevision));
        if (!IsProjectContextCurrent(project, expectedRevision, contextVersion)) return;
        await ApplyGlobalMapLayersAsync(true);
        if (!IsProjectContextCurrent(project, expectedRevision, contextVersion)) return;
        await RebuildDisplayDataAsync(GetProjectZoom(project), expectedRevision);
        if (!IsProjectContextCurrent(project, expectedRevision, contextVersion)) return;
        if (editContextVersion == _trackEditContextVersion && _editingTrack is { } editing &&
            _editingSessionId is Guid editSessionId &&
            IsTrackEditScopeCurrent(project, expectedRevision, editing, _editingGeometryRevision,
                editSessionId, editContextVersion))
            await bridge.SendAsync(MapProtocol.SerializeCommand("BeginTrackEdit", new
            {
                trackId = editing.Id, geometryRevision = _editingGeometryRevision, editSessionId,
                editContextVersion
            }, project.Id, expectedRevision));
        if (!restoreView || !IsProjectContextCurrent(project, expectedRevision, contextVersion)) return;
        var map = project.Settings.Map;
        if (map.Center.HasValue && map.Zoom.HasValue)
        {
            await bridge.SendAsync(MapProtocol.SerializeCommand("SetView", new
            {
                longitude = map.Center.Value.Longitude,
                latitude = map.Center.Value.Latitude,
                zoom = map.Zoom.Value,
                bearing = map.Bearing ?? 0,
                pitch = map.Pitch ?? 0,
                suppressPersistence = true
            }, project.Id, expectedRevision));
        }
        else
        {
            if (project.ActiveTrack is not null) await FitCurrentTrackAsync();
            else await FitAllAsync();
        }
    }

    private async Task RebuildDisplayDataAsync(double zoom, long expectedRevision)
    {
        var project = _project;
        var bridge = _bridge;
        if (project is null || bridge?.IsReady != true) return;
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        _renderCancellation = new CancellationTokenSource();
        var token = _renderCancellation.Token;
        MapStatusText = "正在准备地图轨迹……";
        try
        {
            var data = await Task.Run(() => _displayData.Create(project, zoom, expectedRevision, token,
                _temporaryVisibleTrackId), token);
            var payload = await Task.Run(() => _geoJson.Build(data, project), token);
            if (token.IsCancellationRequested || project != _project || !_revisionGate.IsCurrent(project.Id, expectedRevision)) return;
            _lastDisplayData = data;
            _lastZoomBucket = _profile.GetCacheZoomBucket(zoom);
            await bridge.SendAsync(MapProtocol.SerializeCommand("SetTracks", new { geoJson = ParseJson(payload.TracksGeoJson) }, project.Id, expectedRevision), token);
            var overlays = await Task.Run(() => (
                Sections: new SplitSectionMapGeoJsonBuilder().Build(project, data),
                Arrows: new DirectionArrowGeoJsonBuilder(_arrowCache).Build(project, _temporaryVisibleTrackId, token)), token);
            if (token.IsCancellationRequested || project != _project || !_revisionGate.IsCurrent(project.Id, expectedRevision)) return;
            await bridge.SendAsync(MapProtocol.SerializeCommand("SetSplitSections",
                new { geoJson = ParseJson(overlays.Sections) }, project.Id, expectedRevision), token);
            await bridge.SendAsync(MapProtocol.SerializeCommand("SetTrackDirectionArrows",
                new { geoJson = ParseJson(overlays.Arrows) }, project.Id, expectedRevision), token);
            await SetSplitSectionSelectionAsync(_selectedSplitSectionIds);
            await bridge.SendAsync(MapProtocol.SerializeCommand("SetWaypoints", new { geoJson = ParseJson(payload.WaypointsGeoJson) }, project.Id, expectedRevision), token);
            var guides = await Task.Run(() => new GuideMapGeoJsonBuilder().Build(project), token);
            if (token.IsCancellationRequested || project != _project || !_revisionGate.IsCurrent(project.Id, expectedRevision)) return;
            await bridge.SendAsync(MapProtocol.SerializeCommand("SetGuideNodes", new { geoJson = guides.Nodes }, project.Id, expectedRevision), token);
            await bridge.SendAsync(MapProtocol.SerializeCommand("SetGuideSegments", new { geoJson = guides.Segments }, project.Id, expectedRevision), token);
            await SelectObjectAsync(FindSelectedObject(), cancelPendingFocus: false);
            MapStatusText = $"地图已就绪：显示 {data.Tracks.Sum(track => track.DisplayPointCount):N0} / 原始 {data.Tracks.Sum(track => track.OriginalPointCount):N0} 个轨迹点";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _log.Error(exception, "生成地图显示数据失败");
            MapStatusText = "地图轨迹准备失败，请查看日志。";
        }
    }

    private object? FindSelectedObject()
    {
        if (_project is null || !_selection.ObjectId.HasValue) return null;
        return _selection.Kind switch
        {
            MapSelectionKind.Track => _project.Tracks.FirstOrDefault(value => value.Id == _selection.ObjectId),
            MapSelectionKind.Waypoint => _project.Waypoints.FirstOrDefault(value => value.Id == _selection.ObjectId),
            MapSelectionKind.GuideNode => _project.GuideNodes.FirstOrDefault(value => value.Id == _selection.ObjectId),
            MapSelectionKind.GuideSegment => _project.GuideSegments.FirstOrDefault(value => value.Id == _selection.ObjectId),
            _ => null
        };
    }

    private async Task ApplySelectedSourceAsync(MapSourceDefinition source, bool saveProjectSelection)
    {
        await SelectMapSourceAsync(source);
    }

    public async Task SelectMapSourceAsync(MapSourceDefinition source)
    {
        var s=_settings.Current.MapLayerStack ?? new(); var next=s with {BaseSourceId=source.Id};
        if(MapLayerStackService.Validate(next,_settings.Current.MapSources) is {} error) { UserMessage?.Invoke(this,error); SelectSourceWithoutCallback(MapSources.FirstOrDefault(v=>v.Id==s.BaseSourceId)); return; }
        _settings.Current.MapLayerStack=next; SelectSourceWithoutCallback(source);
        await ApplyGlobalMapLayersAsync();
        try { await _settings.SaveAsync(); } catch(Exception saveError) { _log.Error(saveError,"保存全局地图设置失败"); UserMessage?.Invoke(this,"保存地图设置失败，请检查文件权限或磁盘空间。"); }
    }

    private readonly SemaphoreSlim _mapLayerApplyGate=new(1,1);
    public async Task ApplyGlobalMapLayersAsync(bool force=false)
    {
        await _mapLayerApplyGate.WaitAsync();
        try
        {
        if(_bridge?.IsReady!=true) return;
        var s=MapLayerStackService.Normalize(_settings.Current.MapLayerStack,_settings.Current.MapSources); _settings.Current.MapLayerStack=s;
        var slots=MapLayerStackService.Build(s,_settings.Current.MapSources);
        var signature=System.Text.Json.JsonSerializer.Serialize(new {slots,terrain=_settings.Current.OfflineTerrain,terrain3D=_settings.Current.Terrain3D,osmRoads=_settings.Current.OsmRoads,roadDataRevision=((App)System.Windows.Application.Current).OsmRoads.ConfigurationRevision});
        if(!force && _mapStackSignature==signature) return;
        var settingsRevision=++_mapSettingsRevision;
        slots=_bridge.ConfigureRasterLayers(slots);
        object Payload(RasterLayerSlot slot) => new {sourceId=slot.Source?.Id,name=slot.Source?.Name,templates=slot.Templates,minZoom=slot.Source?.MinZoom??0,maxZoom=slot.Source?.MaxZoom??19,opacity=slot.Opacity,visible=slot.Visible,attribution=slot.Source?.Attribution??""};
        await _bridge.SendAsync(MapProtocol.SerializeCommand("SetMapLayerStack",new {settingsRevision,@base=Payload(slots[0]),overlay1=Payload(slots[1]),overlay2=Payload(slots[2])}));
        var terrainState=_bridge.ConfigureTerrain3D(_settings.Current.Terrain3D??new());
        if(terrainState.Enabled&&!_terrainCameraInitialized&&_settings.Current.TerrainCamera is {IsValid:true} camera)terrainState=terrainState with{Camera=camera};
        _terrainCameraInitialized=true;
        _terrainRevision=terrainState.TerrainRevision;
        var offline=_settings.Current.OfflineTerrain;
        // 2D/3D共用生成器的DEM、显隐及样式；3D仅改变数字贴合方式，不覆盖用户选择。
        offline=offline with{ScreenAlignedLabels=terrainState.Enabled};
        await _bridge.SendAsync(MapProtocol.SerializeCommand("SetTerrainState",terrainState));
        await _bridge.SendAsync(MapProtocol.SerializeCommand("SetOfflineTerrain",_bridge.ConfigureOfflineTerrain(offline)));
        var roads=await ((App)System.Windows.Application.Current).OsmRoads.ConfigureAsync(_settings.Current.OsmRoads,terrainEnabled:terrainState.Enabled);
        if(settingsRevision!=_mapSettingsRevision)return;
        await _bridge.SendAsync(MapProtocol.SerializeCommand("SetOsmRoads",roads));
        if(terrainState.Error is {} terrainError){MapStatusText=terrainError;if(_settings.Current.Terrain3D?.Enabled==true)_ = DisableUnavailableTerrainAsync(terrainError);}
        _mapStackSignature=signature;
        if(_settings.MapSettingsWarning is {} warning) MapStatusText=warning;
        }
        finally{_mapLayerApplyGate.Release();}
    }

    public Task ClearOfflineRegionAsync()=>_bridge?.SendAsync(MapProtocol.SerializeCommand("ClearOfflineRegion",null))??Task.CompletedTask;
    public Task<HikingGuide.Core.Maps.OfflineMapRegion?> SelectOfflineRegionAsync(CancellationToken token=default)=>_bridge?.SelectOfflineRegionAsync(token)??Task.FromResult<HikingGuide.Core.Maps.OfflineMapRegion?>(null);
    public async Task LocateOfflineRegionAsync(HikingGuide.Core.Maps.OfflineMapRegion region,int zoom)
    {
        if(_bridge?.IsReady!=true)return;
        var first=region.Points[0].Longitude;var longitude=region.Points.Average(p=>p.Longitude+360*Math.Round((first-p.Longitude)/360));longitude=((longitude+180)%360+360)%360-180;
        await _bridge.SendAsync(MapProtocol.SerializeCommand("SetView",new{longitude,latitude=region.Points.Average(p=>p.Latitude),zoom=Math.Clamp(zoom,0,22),bearing=0,pitch=0},_project?.Id,_revision));
    }
    public async Task RunInteractionSmokeAsync()
    {
        if (_bridge?.IsReady != true || _project is null) return;
        await _bridge.SendAsync(MapProtocol.SerializeCommand("RunInteractionSmoke", null, _project.Id, _revision));
    }

    public async Task RunP5InteractionSmokeAsync()
    {
        if (_bridge?.IsReady != true || _project is null) return;
        await _bridge.SendAsync(MapProtocol.SerializeCommand("RunP5InteractionSmoke", null, _project.Id, _revision));
    }
    public Task RunP5WaypointEditSmokeAsync() => _bridge?.SendAsync(
        MapProtocol.SerializeCommand("RunP5WaypointEditSmoke", null, _project?.Id, _revision)) ?? Task.CompletedTask;
    public Task RunP5GuideEditSmokeAsync() => _bridge?.SendAsync(
        MapProtocol.SerializeCommand("RunP5GuideEditSmoke", null, _project?.Id, _revision)) ?? Task.CompletedTask;
    public Task RunP5MarkerAddSmokeAsync(double latitude, double longitude) => _bridge?.SendAsync(
        MapProtocol.SerializeCommand("RunP5MarkerAddSmoke", new { latitude, longitude }, _project?.Id, _revision)) ?? Task.CompletedTask;

    public Task CapturePreviewAsync(string outputPath, CancellationToken cancellationToken = default) =>
        _bridge?.CapturePreviewAsync(outputPath, cancellationToken)
        ?? throw new InvalidOperationException("地图尚未初始化，无法保存预览图。");

    public Task FitAllForAutomationAsync() => FitAllAsync();

    private async Task FitCurrentTrackAsync()
    {
        var track = _project?.ActiveTrack;
        if (track is null) return;
        var bounds = MapBounds.FromCoordinates(track.Segments.SelectMany(segment => segment.Points)
            .Select(point => new MapCoordinate(point.Longitude, point.Latitude)));
        await SendBoundsAsync(bounds);
    }

    private async Task FitAllAsync()
    {
        if (_project is null) return;
        var coordinates = _project.Tracks.SelectMany(track => track.Segments).SelectMany(segment => segment.Points)
            .Select(point => new MapCoordinate(point.Longitude, point.Latitude))
            .Concat(_project.Waypoints.Select(point => new MapCoordinate(point.Longitude, point.Latitude)));
        await SendBoundsAsync(MapBounds.FromCoordinates(coordinates));
    }

    private async Task SendBoundsAsync(MapBounds? bounds)
    {
        if (bounds is null || _bridge?.IsReady != true) return;
        await _bridge.SendAsync(MapProtocol.SerializeCommand("FitBounds", new
        {
            west = bounds.Value.West,
            south = bounds.Value.South,
            east = bounds.Value.East,
            north = bounds.Value.North,
            padding = 48
        }, _project?.Id, _revision));
    }

    private async Task ChangeZoomAsync(double delta)
    {
        var map = _settings.Current.Terrain3D?.Enabled==true?null:_project?.Settings.Map;
        var zoom = Math.Clamp((map?.Zoom ?? _lastView?.Zoom ?? _lastDisplayData?.Zoom ?? 12) + delta, 0, 24);
        await _bridge!.SendAsync(MapProtocol.SerializeCommand("SetView", new
        {
            zoom,
            longitude = map?.Center?.Longitude,
            latitude = map?.Center?.Latitude,
            bearing = map?.Bearing ?? _lastView?.Bearing ?? 0,
            pitch = map?.Pitch ?? _lastView?.Pitch ?? 0
            ,terrainUserIntent=true
        }, _project?.Id, _revision));
    }

    private async Task ReloadAsync()
    {
        _reloadTerrainView=_lastView;
        IsReady = false;
        MapStatusText = "正在重新加载地图……";
        if (_bridge is not null) await _bridge.ReloadAsync();
    }

    private void OnInitializationFailed(object? sender, string message)
    {
        IsReady = false;
        MapStatusText = message;
        UserMessage?.Invoke(this, message);
    }

    private void OnMapEvent(object? sender, MapEventMessage value)
    {
        if(HandleTerrainEvent(value))return;
        if (value.Type is "FavoriteClicked" or "FavoriteRightClicked" or "FavoritesSmokeCompleted" or "FavoriteHoverEntered" or "FavoriteHoverLeft" or "FavoriteDragStarted" or "FavoriteDragCompleted" or "FavoriteDragCanceled" or "FavoriteLinkRequested")
        {
            if (value.Revision == _favoritesRevision && value.Type == "FavoriteDragStarted") IsFavoriteDragging = true;
            if (value.Type is "FavoriteDragCompleted" or "FavoriteDragCanceled") IsFavoriteDragging = false;
            if (value.Revision == _favoritesRevision && value.Type is "FavoriteClicked" or "FavoriteRightClicked" &&
                _favorites.FirstOrDefault(p => p.Id == value.FavoriteId) is { } point) _favoriteSelection?.SelectFavorite(point);
            if (value.Revision == _favoritesRevision) FavoritesEvent?.Invoke(this, value);
            if (value.Type == "FavoriteRightClicked" && value.Revision == _favoritesRevision) RightClicked?.Invoke(this, value);
            return;
        }
        if (value.Type != "MapReady" && _project is not null &&
            (value.ProjectId != _project.Id || !_revisionGate.IsCurrent(_project.Id, value.Revision))) return;
        switch (value.Type)
        {
            case "MapReady":
                _terrainCameraInitialized=false;
                _ = UpdateFavoritesAsync(_favorites, _favoritesVisible);
                IsReady = true;
                LastMapLibreReadyMilliseconds = value.DurationMilliseconds;
                MapStatusText = "地图已就绪";
                _ = SynchronizeTerrainReadyAsync();
                break;
            case "ViewChanged":
                _lastView=value;
                if (value.Latitude.HasValue && value.Longitude.HasValue && value.Zoom.HasValue)
                {
                    ZoomText = $"缩放：{value.Zoom:F2}";
                    if(_settings.Current.Terrain3D?.Enabled==true||EffectiveTerrainState)RememberTerrainView(value);
                    else if (!string.Equals(value.Message, "programmatic", StringComparison.Ordinal)) DebounceViewPersistence(value);
                }
                break;
            case "MouseMoved":
                if (value.Latitude.HasValue && value.Longitude.HasValue)
                    MouseCoordinateText = $"经纬度：{value.Latitude.Value.ToString("F6", CultureInfo.InvariantCulture)}, {value.Longitude.Value.ToString("F6", CultureInfo.InvariantCulture)}";
                break;
            case "MouseLeft":
                MouseCoordinateText = "经纬度：—";
                break;
            case "TrackClicked":
                RequestSelection(MapSelectionKind.Track, value.TrackId, value.CtrlKey);
                break;
            case "RoutePlanningPointPicked":
                RoutePlanningPointPicked?.Invoke(this, value);
                break;
            case "SplitSectionClicked":
            case "SplitSectionContextMenuRequested":
                if (value.TrackId is Guid sectionTrackId && value.SplitSectionId is Guid sectionId &&
                    _project?.Tracks.FirstOrDefault(track => track.Id == sectionTrackId) is { } sectionTrack &&
                    sectionTrack.GeometryRevision == value.GeometryRevision &&
                    sectionTrack.GetSplitSections().Any(section => section.Id == sectionId))
                {
                    SplitSectionSelectionRequested?.Invoke(this, value);
                    if (value.Type == "SplitSectionContextMenuRequested") RightClicked?.Invoke(this, value);
                }
                break;
            case "TrackContextMenuRequested":
                if (_project?.Tracks.Any(track => track.Id == value.TrackId) == true)
                    RightClicked?.Invoke(this, value);
                break;
            case "TrackEditLineClicked":
            case "TrackPointDragEnded":
            case "TrackEditPreviewCancelled":
                TrackEditRequested?.Invoke(this, value);
                break;
            case "WaypointClicked":
                RequestSelection(MapSelectionKind.Waypoint, value.WaypointId);
                break;
            case "GuideNodeClicked":
                RequestSelection(MapSelectionKind.GuideNode, value.GuideNodeId);
                break;
            case "GuideSegmentClicked":
                RequestSelection(MapSelectionKind.GuideSegment, value.GuideSegmentId);
                break;
            case "MediaOpenRequested":
                MediaOpenRequested?.Invoke(this, value);
                break;
            case "MediaThumbnailRequested":
                MediaThumbnailRequested?.Invoke(this, value);
                break;
            case "MediaThumbnailReady":
                MediaThumbnailReady?.Invoke(this, value);
                break;
            case "WaypointDragEnded":
                if (value.WaypointId is Guid waypointId && value.Latitude is double waypointLat && value.Longitude is double waypointLon)
                    MarkerEditRequested?.Invoke(this, new MapMarkerEditRequest(MapMarkerEditKind.MoveWaypoint, waypointId, new GeoCoordinate(waypointLat, waypointLon)));
                break;
            case "GuideNodeDragEnded":
                if (value.GuideNodeId is Guid nodeId && value.Latitude is double nodeLat && value.Longitude is double nodeLon)
                    MarkerEditRequested?.Invoke(this, new MapMarkerEditRequest(MapMarkerEditKind.MoveGuideNode, nodeId, new GeoCoordinate(nodeLat, nodeLon)));
                break;
            case "TrackSnapLocationPicked":
                if (value.GuideNodeId is Guid pickedNodeId && value.Latitude is double pickedLat && value.Longitude is double pickedLon)
                    SnapLocationPicked?.Invoke(this, new MapSnapLocationPick(pickedNodeId, new GeoCoordinate(pickedLat, pickedLon)));
                break;
            case "MarkerAddLocationPicked":
                if (value.Latitude is double addLat && value.Longitude is double addLon)
                {
                    var kind = value.Message == "guide" ? MapMarkerEditKind.AddGuideNode : MapMarkerEditKind.AddWaypoint;
                    MarkerEditRequested?.Invoke(this, new MapMarkerEditRequest(kind, null, new GeoCoordinate(addLat, addLon)));
                }
                break;
            case "MapRightClicked":
            case "MarkerRightClicked":
                RightClicked?.Invoke(this, value);
                break;
            case "MapError":
                MapStatusText = $"地图错误：{value.Message ?? "未知错误"}";
                _log.Warning($"[地图][JavaScript] {value.Message ?? "未知错误"}");
                break;
            case "TileError":
                LastTileErrorMessage=value.Message;
                MapStatusText = value.Message ?? "地图瓦片加载失败，请检查网络或图源设置。";
                if (DateTimeOffset.UtcNow - _lastTileErrorLog > TimeSpan.FromSeconds(30))
                {
                    _lastTileErrorLog = DateTimeOffset.UtcNow;
                    _log.Warning("[地图] 当前图源部分地图瓦片加载失败，详细地址未写入日志以避免泄露敏感参数。");
                }
                break;
            case "RenderMetrics":
                if (value.DurationMilliseconds.HasValue)
                {
                    MapStatusText = $"地图已就绪，显示数据更新用时 {value.DurationMilliseconds.Value:F0} 毫秒";
                    RenderCompleted?.Invoke(this, value.DurationMilliseconds.Value);
                }
                break;
            case "InteractionSmokeCompleted":
                InteractionSmokeCompleted?.Invoke(this, value.Message ?? "交互测试完成");
                break;
        }
    }

    private void RequestSelection(MapSelectionKind kind, Guid? id, bool control = false)
    {
        if (_project is null || !id.HasValue) return;
        var exists = kind switch
        {
            MapSelectionKind.Track => _project.Tracks.Any(track => track.Id == id.Value),
            MapSelectionKind.Waypoint => _project.Waypoints.Any(waypoint => waypoint.Id == id.Value),
            MapSelectionKind.GuideNode => _project.GuideNodes.Any(node => node.Id == id.Value),
            MapSelectionKind.GuideSegment => _project.GuideSegments.Any(segment => segment.Id == id.Value),
            _ => false
        };
        if (!exists) return;
        _selection = new MapSelection(kind, id, control);
        SelectionRequested?.Invoke(this, _selection);
        _ = SelectObjectAsync(FindSelectedObject());
    }

    private void DebounceViewPersistence(MapEventMessage value)
    {
        _viewCancellation?.Cancel();
        _viewCancellation?.Dispose();
        _viewCancellation = new CancellationTokenSource();
        var token = _viewCancellation.Token;
        var project = _project;
        var revision = _revision;
        _ = PersistAfterDelayAsync(project, revision, value, token);
    }

    private async Task PersistAfterDelayAsync(HikingProject? project, long revision, MapEventMessage value, CancellationToken token)
    {
        try
        {
            await Task.Delay(500, token);
            if (project is null || project != _project || !_revisionGate.IsCurrent(project.Id, revision)) return;
            project.ExecuteAtomicUpdate(active =>
            {
                active.Settings.Map.Center = new GeoCoordinate(value.Latitude!.Value, value.Longitude!.Value);
                active.Settings.Map.Zoom = value.Zoom!.Value;
                active.Settings.Map.Bearing = value.Bearing ?? 0;
                active.Settings.Map.Pitch = value.Pitch ?? 0;
                active.MarkDirty();
            });
            var bucket = _profile.GetCacheZoomBucket(value.Zoom!.Value);
            if (bucket != _lastZoomBucket) await RebuildDisplayDataAsync(value.Zoom.Value, revision);
        }
        catch (OperationCanceledException) { }
    }

    private void SelectSourceWithoutCallback(MapSourceDefinition? value)
    {
        _selectedMapSource = value;
        OnPropertyChanged(nameof(SelectedMapSource));
        OnPropertyChanged(nameof(SelectedMapSourceId));
    }

    private static double GetProjectZoom(HikingProject project) => project.Settings.Map.Zoom ?? 14;
    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private void RaiseCommandStates()
    {
        ((AsyncRelayCommand)FitCurrentTrackCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)FitAllCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ZoomInCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ZoomOutCommand).RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _settings.MapSettingsChanged -= _onMapSettingsChanged;
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        _viewCancellation?.Cancel();
        _viewCancellation?.Dispose();
        if (_bridge is not null)
        {
            _bridge.EventReceived -= OnMapEvent;
            _bridge.InitializationFailed -= OnInitializationFailed;
        }
    }
}

public enum MapMarkerEditKind { AddWaypoint, AddGuideNode, MoveWaypoint, MoveGuideNode }
public sealed record MapMarkerEditRequest(MapMarkerEditKind Kind, Guid? ObjectId, GeoCoordinate Coordinate);
public sealed record MapSnapLocationPick(Guid GuideNodeId, GeoCoordinate Coordinate);




