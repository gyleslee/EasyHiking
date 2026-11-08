using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

public static class TrackDisplayColor
{
    public static bool IsValid(string? color) => color is { Length: 7 } && color[0] == '#' &&
        color.AsSpan(1).IndexOfAnyExcept("0123456789ABCDEFabcdef") < 0;

    public static string NextUnique(IEnumerable<Track> existingTracks)
    {
        var used = existingTracks.Select(track => track.DisplayColor).Where(IsValid)
            .Select(color => color!.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        used.Add("#2877C7");
        for (var attempt = 0; attempt < 100_000; attempt++)
        {
            var hue = Random.Shared.Next(360);
            var saturation = Random.Shared.Next(65, 86) / 100d;
            var lightness = Random.Shared.Next(39, 57) / 100d;
            var color = FromHsl(hue, saturation, lightness);
            if (used.Add(color)) return color;
        }
        throw new InvalidOperationException("无法为新轨迹分配不重复的显示颜色。");
    }

    public static string Normalize(string color)
    {
        if (!IsValid(color)) throw new ArgumentException("颜色必须为 #RRGGBB 格式。", nameof(color));
        return color.ToUpperInvariant();
    }

    private static string FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        var match = lightness - chroma / 2;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return $"#{(byte)Math.Round((r + match) * 255):X2}{(byte)Math.Round((g + match) * 255):X2}{(byte)Math.Round((b + match) * 255):X2}";
    }
}
