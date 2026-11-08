namespace HikingGuide.Core.Models;

/// <summary>项目内地图图层显隐设置。缺失的键默认显示，绝不修改轨迹或攻略分段数据。</summary>
public static class MapLayerVisibility
{
    public static string TrackKey(Guid trackId) => $"track:{trackId:D}";
    public static string GuideSegmentKey(Guid segmentId) => $"guide-segment:{segmentId:D}";

    public static bool IsTrackVisible(MapProjectSettings settings, Guid trackId) =>
        IsVisible(settings, TrackKey(trackId));

    public static bool IsGuideSegmentVisible(MapProjectSettings settings, Guid segmentId) =>
        IsVisible(settings, GuideSegmentKey(segmentId));

    public static void SetTrackVisible(MapProjectSettings settings, Guid trackId, bool visible) =>
        SetVisible(settings, TrackKey(trackId), visible);

    public static void SetGuideSegmentVisible(MapProjectSettings settings, Guid segmentId, bool visible) =>
        SetVisible(settings, GuideSegmentKey(segmentId), visible);

    private static bool IsVisible(MapProjectSettings settings, string key) =>
        !settings.LayerVisibility.TryGetValue(key, out var visible) || visible;

    private static void SetVisible(MapProjectSettings settings, string key, bool visible)
    {
        if (visible) settings.LayerVisibility.Remove(key);
        else settings.LayerVisibility[key] = false;
    }
}
