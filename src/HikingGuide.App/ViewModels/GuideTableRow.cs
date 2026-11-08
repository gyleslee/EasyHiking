using HikingGuide.Core.Models;

namespace HikingGuide.App.ViewModels;

public sealed record GuideTableRow(int Order, GuideNode Node, GuideSegment? IncomingSegment, double WarningDistanceMeters)
{
    public string Name => Node.Name ?? "未命名攻略节点";
    public string TypeName => MarkerTypeNames.Get(Node.Type, Node.CustomTypeName);
    public string DistanceKm => Node.DistanceFromStart is double value ? (value / 1000).ToString("F2") : "—";
    public string SegmentKm => IncomingSegment is null ? "—" : (IncomingSegment.Distance / 1000).ToString("F2");
    public string Status => Node.IsOrphaned ? "失联" : Node.DistanceToTrack > WarningDistanceMeters ? "距轨迹较远" : "正常";
}

public static class MarkerTypeNames
{
    public static string Get(WaypointType type, string? customName = null) => type switch
    {
        WaypointType.Normal => "普通", WaypointType.Start => "起点", WaypointType.Finish => "终点",
        WaypointType.Summit => "山峰", WaypointType.Pass => "垭口", WaypointType.Junction => "岔路",
        WaypointType.Camp => "营地", WaypointType.Water => "水源", WaypointType.Resupply => "补给",
        WaypointType.Village => "村庄", WaypointType.Accommodation => "住宿", WaypointType.Transport => "交通（旧版）",
        WaypointType.PublicTransport => "公共交通", WaypointType.Evacuation => "下撤点", WaypointType.Danger => "危险点",
        WaypointType.Viewpoint => "观景点", WaypointType.Checkpoint => "检查站", WaypointType.Toilet => "卫生间",
        WaypointType.Parking => "停车", WaypointType.Other => "其它", WaypointType.Custom => customName ?? "自定义",
        _ => "未识别类型"
    };
}
