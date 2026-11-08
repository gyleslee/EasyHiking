namespace HikingGuide.Core.Models;

public abstract class MapMarkerBase
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string? Name { get; set; }

    public string? Description { get; set; }

    public WaypointType Type { get; set; } = WaypointType.Normal;

    public string? CustomTypeName { get; set; }

    public double? SourceElevation { get; set; }

    public double? DemElevation { get; set; }

    /// <summary>标注点时间，保留原始时区语义。</summary>
    public DateTimeOffset? Timestamp { get; set; }

    public Dictionary<string, string?> OriginalAttributes { get; set; } = [];

    /// <summary>转换为普通标注后仍保留的人工攻略信息。</summary>
    public ManualGuideData? ManualData { get; set; }
}
