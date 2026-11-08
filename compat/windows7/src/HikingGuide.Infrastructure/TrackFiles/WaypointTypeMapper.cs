using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.TrackFiles;

internal static class WaypointTypeMapper
{
    public static WaypointType FromExplicitValues(params string?[] values)
    {
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            switch (value!.Trim().ToLowerInvariant())
            {
                case "start":
                case "trail head":
                case "trailhead":
                case "起点":
                    return WaypointType.Start;
                case "finish":
                case "end":
                case "终点":
                    return WaypointType.Finish;
                case "summit":
                case "peak":
                case "山峰":
                    return WaypointType.Summit;
                case "pass":
                case "垭口":
                    return WaypointType.Pass;
                case "camp":
                case "campground":
                case "营地":
                    return WaypointType.Camp;
                case "water":
                case "drinking water":
                case "水源":
                    return WaypointType.Water;
                case "resupply":
                case "补给":
                    return WaypointType.Resupply;
                case "village":
                case "村庄":
                    return WaypointType.Village;
                case "transport":
                case "交通":
                    return WaypointType.Transport;
                case "public transport":
                case "publictransport":
                case "公共交通":
                    return WaypointType.PublicTransport;
                case "accommodation":
                case "lodging":
                case "住宿":
                    return WaypointType.Accommodation;
                case "checkpoint":
                case "检查站":
                    return WaypointType.Checkpoint;
                case "toilet":
                case "restroom":
                case "卫生间":
                    return WaypointType.Toilet;
                case "parking":
                case "parking area":
                case "停车":
                    return WaypointType.Parking;
                case "other":
                case "其它":
                    return WaypointType.Other;
                case "junction":
                case "岔路":
                    return WaypointType.Junction;
                case "evacuation":
                case "下撤点":
                    return WaypointType.Evacuation;
                case "danger":
                case "危险点":
                    return WaypointType.Danger;
                case "viewpoint":
                case "观景点":
                    return WaypointType.Viewpoint;
            }
        }

        return WaypointType.Normal;
    }
}
