using System.Net;
using System.Text.RegularExpressions;

namespace HikingGuide.Infrastructure.TrackFiles;

internal static partial class KmlDescriptionSanitizer
{
    [GeneratedRegex("<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImageTag();
    [GeneratedRegex("<a\\b[^>]*>\\s*<img\\b[^>]*>\\s*</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImageLink();
    [GeneratedRegex("<div\\b[^>]*>\\s*</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex EmptyMediaDiv();
    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();
    [GeneratedRegex("</?a\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorTag();
    [GeneratedRegex("</?div\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DivTag();
    [GeneratedRegex("(?:\\r?\\n[ \\t]*){2,}")]
    private static partial Regex ExcessBlankLines();

    public static string Clean(string? description)
    {
        if (string.IsNullOrEmpty(description)) return "";
        var text = description;
        if (!text.Contains("<img", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("<div", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("&lt;img", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("&lt;div", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("&amp;lt;", StringComparison.OrdinalIgnoreCase))
            return text;
        // 历史 KML 描述可能经过 XML 和 HTML 两层编码；只在识别到媒体包装时解码。
        for (var depth = 0; depth < 3 &&
             (text.Contains("&lt;", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("&amp;lt;", StringComparison.OrdinalIgnoreCase)); depth++)
            text = WebUtility.HtmlDecode(text);
        text = ImageLink().Replace(text, "");
        text = ImageTag().Replace(text, "");
        text = AnchorTag().Replace(text, "");
        text = DivTag().Replace(text, "\n");
        text = EmptyMediaDiv().Replace(text, "");
        text = BreakTag().Replace(text, "\n");
        return ExcessBlankLines().Replace(WebUtility.HtmlDecode(text), "\n").Trim();
    }
}
