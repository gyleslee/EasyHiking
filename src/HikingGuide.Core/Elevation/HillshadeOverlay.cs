namespace HikingGuide.Core.Elevation;

/// <summary>地图阴影的逐像素不透明度；只压暗背光区域，不覆盖平地和受光面的底图颜色。</summary>
public static class HillshadeOverlay
{
    // 单独更新阴影身份，不使未改变的等高线缓存失效。
    public const string AlgorithmVersion = "shadow-alpha-linear-nodes-1";

    public static double Opacity(double west, double east, double north, double south, double groundStep)
    {
        if (!double.IsFinite(west) || !double.IsFinite(east) || !double.IsFinite(north) ||
            !double.IsFinite(south) || !double.IsFinite(groundStep) || groundStep <= 0) return 0;
        var flatLight = Math.Sin(TerrainTileMath.LightAltitude * Math.PI / 180);
        var light = TerrainTileMath.Shade(west, east, north, south, groundStep);
        if (!double.IsFinite(light)) return 0;
        var shadow = Math.Clamp((flatLight - light) / flatLight, 0, 1);
        // Smoothstep令亮暗交界连续平滑，浅坡不会形成均匀的灰色底罩。
        return shadow * shadow * (3 - 2 * shadow);
    }
}
