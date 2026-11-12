namespace HikingGuide.Core.Models;

/// <summary>软件级设置模型，不进入 .hikeproj 项目容器。</summary>
public sealed class GlobalSettings
{
    public int MarkerDisplayMode { get; set; } = 3;
    /// <summary>P3 兼容字段；P4 使用强类型 MapSources。</summary>
    public List<string> MapSourceDefinitions { get; set; } = [];
    public List<MapSourceDefinition> MapSources { get; set; } = [];
    public Guid? DefaultMapSourceId { get; set; }
    public GlobalMapLayerStackSettings? MapLayerStack { get; set; }
    public OfflineTerrainSettings OfflineTerrain { get; set; } = new();
    public OsmRoadSettings OsmRoads {get;set;}=new();
    public Terrain3DSettings? Terrain3D { get; set; }
    public int UnifiedMapDemVersion {get;set;}
    public TerrainCameraState? TerrainCamera {get;set;}
    public PathReference? MapCacheDirectory { get; set; }
    public PathReference? DefaultDemDirectory { get; set; }
    public PathReference? DefaultExcelTemplate { get; set; }
    public PathReference? DefaultExportDirectory { get; set; }
    public int AutoSaveIntervalMinutes { get; set; } = 5;
    public double FlatSlopeThresholdPercent { get; set; } = new HikingGuide.Core.Elevation.LocusTerrainDistanceProfile().FlatSlopeThresholdPercent;
    public bool ShowFavoritesOnMap { get; set; } = true;
    public bool FirstRunSetupCompleted { get; set; }
    public MainWindowPanelLayout? PanelLayout { get; set; } = MainWindowPanelLayout.Default;
    public PanelVisibilitySettings PanelVisibility { get; set; } = new();
    public Dictionary<string, double> GuideTableColumnWidths { get; set; } = [];
}

/// <summary>全局面板显隐；旧配置缺少此字段时维持全部显示，不影响项目内容。</summary>
public sealed record PanelVisibilitySettings
{
    public bool Project { get; init; } = true;
    public bool Map { get; init; } = true;
    public bool Properties { get; init; } = true;
    public bool Guide { get; init; } = true;
    public bool Planning { get; init; } = true;
    public bool Analysis { get; init; } = true;
    public bool Favorites { get; init; } = true;
    public bool Status { get; init; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AnyUpper => Project || Map || Properties;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AnyBottom => Guide || Planning || Analysis || Favorites;
}

/// <summary>只保存软件面板比例，不进入项目文件；比例适配不同窗口/屏幕大小。</summary>
public sealed record MainWindowPanelLayout(double BottomFraction, double ProjectFraction, double PropertiesFraction)
{
    public static MainWindowPanelLayout Default { get; } = new(.2785825718743035, .12742911755336098, .12742911755336098);
    public bool IsValid => double.IsFinite(BottomFraction) && BottomFraction > 0 && BottomFraction < 1 &&
        double.IsFinite(ProjectFraction) && ProjectFraction > 0 &&
        double.IsFinite(PropertiesFraction) && PropertiesFraction > 0 &&
        ProjectFraction + PropertiesFraction < 1;
}
