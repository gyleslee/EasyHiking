namespace HikingGuide.Core.Models;

/// <summary>地图派生显示层；独立于攻略高程与三个在线图层。</summary>
public sealed record OfflineTerrainSettings
{
    public Guid? DemDatabaseId { get; init; }
    public bool ContoursVisible { get; init; } = true;
    public string ContourColor { get; init; } = "#8B5A2B";
    public double ContourOpacity { get; init; } = 1;
    public bool MajorContoursEnabled {get;init;}=true;
    public int MajorContourIntervalMeters {get;init;}=50;
    public int ContourCacheMb { get; init; } = 2048;
    public bool HillshadeVisible { get; init; } = true;
    public double HillshadeOpacity { get; init; } = .3;
    public int HillshadeCacheMb { get; init; } = 1024;
    /// <summary>仅供3D临时显示上下文使用；正常2D设置保持false。</summary>
    public bool ScreenAlignedLabels {get;init;}
    public bool IsValid => MajorContourIntervalMeters>0 && ContourCacheMb is >= 0 and <= 1048576 && HillshadeCacheMb is >= 0 and <= 1048576 &&
        double.IsFinite(HillshadeOpacity) && HillshadeOpacity is >= 0 and <= 1 &&
        double.IsFinite(ContourOpacity) && ContourOpacity is >= 0 and <= 1 &&
        ContourColor is { Length: 7 } && ContourColor[0] == '#' && ContourColor.Skip(1).All(Uri.IsHexDigit);
}
