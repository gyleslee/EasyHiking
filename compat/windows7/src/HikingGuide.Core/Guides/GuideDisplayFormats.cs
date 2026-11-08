using System.Globalization;

namespace HikingGuide.Core.Guides;

/// <summary>共享显示精度，不修改分析结果或人工计划的原始精度。</summary>
public static class GuideDisplayFormats
{
    public static int Decimals(string key) => key switch
    {
        "Elevation" or "CumulativeAscent" or "SegmentAscent" or "CumulativeDescent" or "SegmentDescent" => 0,
        "DistanceFromStart" or "SegmentDistance" or "UphillDistance" or "DownhillDistance" or "FlatDistance" => 1,
        _ => 2
    };
    public static string Number(double value, int decimals, IFormatProvider? culture = null) =>
        Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), culture ?? CultureInfo.CurrentCulture);
    public static string Summary(GuideDailyItinerary day) => $"D{day.DayNumber}距离{(day.DistanceMeters is double distance ? Number(distance / 1000, 1, CultureInfo.InvariantCulture) : "待计算")}KM，爬升{(day.AscentMeters is double up ? Number(up, 0, CultureInfo.InvariantCulture) : "待计算")}米，下降{(day.DescentMeters is double down ? Number(down, 0, CultureInfo.InvariantCulture) : "待计算")}米，计划总用时" +
        (day.PlannedDurationHours is double hours ? Number(hours, 1, CultureInfo.InvariantCulture) + "h" : "未填写完整");
}
