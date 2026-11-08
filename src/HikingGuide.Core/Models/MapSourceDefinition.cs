namespace HikingGuide.Core.Models;

[Flags]
public enum MapSourceUsage { Base = 1, Overlay = 2, Both = Base | Overlay }

public enum MapSourceRole
{
    BaseLayer,
    Overlay
}

/// <summary>标准Web Mercator XYZ瓦片所使用的地理坐标基准；不改变项目坐标。</summary>
public enum MapSourceCoordinateSystem { Wgs84, Gcj02, Bd09 }

public sealed class MapSourceDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "未命名图源";
    public string UrlTemplate { get; set; } = "";
    public MapSourceCoordinateSystem CoordinateSystem { get; set; } = MapSourceCoordinateSystem.Wgs84;
    [System.Text.Json.Serialization.JsonIgnore]
    public string CoordinateSystemText => CoordinateSystem switch
    {
        MapSourceCoordinateSystem.Wgs84 => "WGS-84",
        MapSourceCoordinateSystem.Gcj02 => "GCJ-02（高德／腾讯）",
        MapSourceCoordinateSystem.Bd09 => "BD-09（标准XYZ）",
        _ => "不支持的坐标系"
    };
    public int CacheCapacityMb {get;set;} = 500;
    public int MinZoom { get; set; }
    public int MaxZoom { get; set; } = 19;
    /// <summary>内部统一使用 0～1。</summary>
    public double Opacity { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public string? UserAgent { get; set; }
    public string? Referer { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];
    public string? Attribution { get; set; }
    public MapSourceRole Role { get; set; } = MapSourceRole.BaseLayer;
    public MapSourceUsage? Usage { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public MapSourceUsage EffectiveUsage => Usage ?? (Role == MapSourceRole.Overlay ? MapSourceUsage.Overlay : MapSourceUsage.Both);
    [System.Text.Json.Serialization.JsonIgnore]
    public string UsageText => EffectiveUsage switch { MapSourceUsage.Base => "底图", MapSourceUsage.Overlay => "叠加层", _ => "底图及叠加层" };

    public MapSourceDefinition DeepCopy() => new()
    {
        Id = Id,
        Name = Name,
        UrlTemplate = UrlTemplate,
        CoordinateSystem = CoordinateSystem,
        CacheCapacityMb = CacheCapacityMb,
        MinZoom = MinZoom,
        MaxZoom = MaxZoom,
        Opacity = Opacity,
        Enabled = Enabled,
        UserAgent = UserAgent,
        Referer = Referer,
        Headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
        Attribution = Attribution,
        Role = Role,
        Usage = Usage
    };
}
