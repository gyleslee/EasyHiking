using System.Globalization;
using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Models;

/// <summary>官方 rd5 的 5°×5°西南角；与中国 1°清单严格分离。</summary>
public readonly record struct BRouterTile(int SouthLatitude, int WestLongitude)
{
    // 官方命名在个位数时不补零，例如 E5_N45.rd5。
    public string FileName => $"{(WestLongitude < 0 ? 'W' : 'E')}{Math.Abs(WestLongitude)}_{(SouthLatitude < 0 ? 'S' : 'N')}{Math.Abs(SouthLatitude)}.rd5";
    public GeoBoundingBox Bounds => new(SouthLatitude, SouthLatitude + 5, WestLongitude, WestLongitude + 5);

    public static BRouterTile At(GeoCoordinate point)
    {
        point.Validate();
        // 180° 与 90° 分别归属最后一格，不生成越界西南角。
        var lat = Math.Min(point.Latitude, Math.BitDecrement(90d));
        var lon = Math.Min(point.Longitude, Math.BitDecrement(180d));
        return new((int)Math.Floor(lat / 5) * 5, (int)Math.Floor(lon / 5) * 5);
    }

    public static BRouterTile ParseOneDegreeCell(string value)
    {
        if (value is null || value.Length != 7 ||
            value[0] is not ('N' or 'S') || value[3] is not ('E' or 'W') ||
            !int.TryParse(value.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var lat) ||
            !int.TryParse(value.AsSpan(4, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var lon) ||
            lat > 89 || lon > 179)
            throw new FormatException($"无效的1°瓦片名称：{value}");
        return At(new GeoCoordinate(value[0] == 'S' ? -lat : lat,
            value[3] == 'W' ? -lon : lon));
    }

    public static BRouterTile ParseFileName(string value)
    {
        if (value is null || !System.Text.RegularExpressions.Regex.IsMatch(value,
                @"^[EW][0-9]{1,3}_[NS][0-9]{1,2}\.rd5$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new FormatException($"无效的BRouter路由瓦片名称：{value}");
        var underscore = value.IndexOf('_');
        var lon = int.Parse(value.AsSpan(1, underscore - 1), CultureInfo.InvariantCulture);
        var lat = int.Parse(value.AsSpan(underscore + 2, value.Length - underscore - 6), CultureInfo.InvariantCulture);
        if (value[0] == 'W') lon = -lon;
        if (value[underscore + 1] == 'S') lat = -lat;
        if (lon is < -180 or > 175 || lat is < -90 or > 85 || lon % 5 != 0 || lat % 5 != 0)
            throw new FormatException($"BRouter路由瓦片超出5°网格：{value}");
        var tile = new BRouterTile(lat, lon);
        if (tile.FileName != value) throw new FormatException($"BRouter路由瓦片名称不规范：{value}");
        return tile;
    }
}
