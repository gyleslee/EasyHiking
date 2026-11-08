using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Models;

public sealed class ProjectSettings
{
    public MapProjectSettings Map { get; set; } = new();
    public DemProjectSettings Dem { get; set; } = new();
    public RoutingProjectSettings Routing { get; set; } = new();
    public ExcelProjectSettings Excel { get; set; } = new();
    public GuideNodeSnapProfile GuideSnap { get; set; } = new();
}

public sealed class MapProjectSettings
{
    /// <summary>P3 兼容字段；P4 新项目使用 SelectedMapSourceId。</summary>
    public string? SourceId { get; set; }
    /// <summary>Legacy：兼容旧项目读写；从P10-1起不再决定主地图图层选择。</summary>
    public Guid? SelectedMapSourceId { get; set; }
    public GeoCoordinate? Center { get; set; }
    public double? Zoom { get; set; }
    public double? Bearing { get; set; }
    public double? Pitch { get; set; }
    public Dictionary<string, bool> LayerVisibility { get; set; } = [];
}

public sealed class DemProjectSettings
{
    public string? ProfileId { get; set; }
    public PathReference? DataDirectory { get; set; }
}

public sealed class RoutingProjectSettings
{
    public string? ProfileId { get; set; }
    public string? ServiceId { get; set; }
}

public sealed class ExcelProjectSettings
{
    public PathReference? Template { get; set; }
    public PathReference? ExportDirectory { get; set; }
}
