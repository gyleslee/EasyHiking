using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Serialization;
using HikingGuide.Core.Elevation;
using HikingGuide.Core.Guides;

namespace HikingGuide.Core.Models;

public sealed class HikingProject : IJsonOnDeserialized
{
    private string _name = "未命名项目";
    private string? _description;
    private Guid? _activeTrackId;
    private Guid? _mainGuideTrackId;
    private ManualGuideData _manualData = new();
    private AnalysisProfile _analysisProfile = new();
    private AnalysisResult? _analysisResult;
    private bool _isAnalysisResultStale;
    private ProjectSettings _settings = new();
    private GuideWorkbenchData _guideData = new();

    public HikingProject()
    {
        Tracks.CollectionChanged += OnTracksChanged;
        Waypoints.CollectionChanged += OnCollectionChanged;
        GuideNodes.CollectionChanged += OnCollectionChanged;
        GuideSegments.CollectionChanged += OnCollectionChanged;
        RetiredGuideSegments.CollectionChanged += OnCollectionChanged;
        RouteControlPoints.CollectionChanged += OnCollectionChanged;
        ElevationAnalyses.CollectionChanged += OnCollectionChanged;
        MediaBlobs.CollectionChanged += OnCollectionChanged;
        MediaAttachments.CollectionChanged += OnCollectionChanged;
        ExternalMediaReferences.CollectionChanged += OnCollectionChanged;
    }

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set => SetAndMarkDirty(ref _name, value);
    }

    public string? Description
    {
        get => _description;
        set => SetAndMarkDirty(ref _description, value);
    }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<Track> Tracks { get; } = [];

    public Guid? ActiveTrackId
    {
        get => _activeTrackId;
        set
        {
            if (value.HasValue && Tracks.All(track => track.Id != value.Value))
            {
                throw new ArgumentException("当前工作轨迹必须属于本项目。", nameof(value));
            }

            // 仅为当前界面选中状态，切换轨迹不构成项目业务修改。
            _activeTrackId = value;
        }
    }

    [JsonIgnore]
    public Track? ActiveTrack => ActiveTrackId is Guid id
        ? Tracks.FirstOrDefault(track => track.Id == id)
        : null;

    /// <summary>唯一负责攻略节点吸附与正式攻略分段的轨迹；与界面选中轨迹无关。</summary>
    public Guid? MainGuideTrackId
    {
        get => _mainGuideTrackId;
        set
        {
            if (value.HasValue && Tracks.All(track => track.Id != value.Value))
                throw new ArgumentException("主攻略轨迹必须属于本项目。", nameof(value));
            if (_mainGuideTrackId != value) InvalidateGuideDerived();
            SetAndMarkDirty(ref _mainGuideTrackId, value);
        }
    }

    [JsonIgnore]
    public Track? MainGuideTrack => MainGuideTrackId is Guid id
        ? Tracks.FirstOrDefault(track => track.Id == id) : null;

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<Waypoint> Waypoints { get; } = [];

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<GuideNode> GuideNodes { get; } = [];

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<GuideSegment> GuideSegments { get; } = [];

    /// <summary>不再相邻的旧分段，保留稳定 ID 与人工填写内容，重现相邻关系时可恢复。</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<GuideSegment> RetiredGuideSegments { get; } = [];

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<RouteControlPoint> RouteControlPoints { get; } = [];

    /// <summary>P8 多来源分析快照；DEM原文件及全局挂载路径不进入项目。</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<ElevationAnalysisResult> ElevationAnalyses { get; } = [];

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<MediaBlob> MediaBlobs { get; } = [];

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<MediaAttachment> MediaAttachments { get; } = [];

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<ExternalMediaReference> ExternalMediaReferences { get; } = [];

    public ManualGuideData ManualData
    {
        get => _manualData;
        set => SetAndMarkDirty(ref _manualData, value);
    }

    public AnalysisProfile AnalysisProfile
    {
        get => _analysisProfile;
        set => SetAndMarkDirty(ref _analysisProfile, value);
    }

    public AnalysisResult? AnalysisResult
    {
        get => _analysisResult;
        set => SetAndMarkDirty(ref _analysisResult, value);
    }

    public bool IsAnalysisResultStale
    {
        get => _isAnalysisResultStale;
        set => SetAndMarkDirty(ref _isAnalysisResultStale, value);
    }

    public ProjectSettings Settings
    {
        get => _settings;
        set => SetAndMarkDirty(ref _settings, value ?? new ProjectSettings());
    }

    public int ProjectVersion { get; set; } = 3;

    /// <summary>P10项目独立攻略工作台数据。旧项目加载时创建空白默认配置，不采用模板示例速度。</summary>
    public GuideWorkbenchData GuideData
    {
        get => _guideData;
        set => SetAndMarkDirty(ref _guideData, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public void UpdateGuideData(Action<GuideWorkbenchData> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (this) { update(GuideData); MarkDirty(); }
    }

    /// <summary>为后续项目文件字段保留未知 JSON 数据，P1 不解释这些设置。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public bool IsDirty { get; private set; }

    public void UpdateManualData(Action<ManualGuideData> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        update(ManualData);
        MarkDirty();
    }

    /// <summary>在与后台项目快照相同的同步边界内执行一组领域修改。</summary>
    public void ExecuteAtomicUpdate(Action<HikingProject> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (this) update(this);
    }

    /// <summary>
    /// 轨迹点或轨迹元数据被原位修改后调用；替换整个 Track 时会自动标记。
    /// </summary>
    public void NotifyTrackChanged() { InvalidateGuideDerived(); MarkDirty(); }

    /// <summary>轨迹几何修改后使旧吸附位置失效；保留节点和分段人工数据供重新关联。</summary>
    public void NotifyTrackGeometryChanged(Guid trackId)
    {
        if (Tracks.All(track => track.Id != trackId)) throw new ArgumentException("轨迹不属于本项目。", nameof(trackId));
        foreach (var node in GuideNodes.Where(node => node.TrackId == trackId)) node.IsOrphaned = true;
        if (trackId == MainGuideTrackId) InvalidateGuideDerived();
        MarkDirty();
    }

    public void AddTrack(Track track, bool makeActiveWhenNone = true)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Id == Guid.Empty || Tracks.Any(existing => existing.Id == track.Id))
        {
            throw new ArgumentException("轨迹 ID 为空或已存在。", nameof(track));
        }

        Tracks.Add(track);
        if (makeActiveWhenNone && ActiveTrackId is null)
        {
            ActiveTrackId = track.Id;
        }
        if (makeActiveWhenNone && MainGuideTrackId is null)
            MainGuideTrackId = track.Id;
    }

    public bool RemoveTrack(Guid trackId)
    {
        var track = Tracks.FirstOrDefault(item => item.Id == trackId);
        if (track is null)
        {
            return false;
        }

        Tracks.Remove(track);
        return true;
    }

    public void SetActiveTrack(Guid trackId) => ActiveTrackId = trackId;

    /// <summary>攻略节点属性被原位修改后调用；集合增删会自动标记。</summary>
    public void NotifyGuideNodeChanged() => MarkDirty();

    public void MarkDirty()
    {
        IsDirty = true;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void MarkSaved() => IsDirty = false;

    void IJsonOnDeserialized.OnDeserialized() => MarkSaved();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, GuideNodes)) InvalidateGuideDerived();
        MarkDirty();
    }

    private void InvalidateGuideDerived()
    { if (_guideData.Derived is { IsStale: false } derived) _guideData.Derived = derived with { IsStale = true }; }

    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateGuideDerived();
        if (_activeTrackId.HasValue && Tracks.All(track => track.Id != _activeTrackId.Value))
        {
            _activeTrackId = Tracks.FirstOrDefault()?.Id;
        }

        if (_mainGuideTrackId.HasValue && Tracks.All(track => track.Id != _mainGuideTrackId.Value))
            _mainGuideTrackId = null;

        MarkDirty();
    }

    private void SetAndMarkDirty<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        MarkDirty();
    }
}
