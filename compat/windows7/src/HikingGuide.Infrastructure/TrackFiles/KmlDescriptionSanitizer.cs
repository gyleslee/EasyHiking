using System.Net;
using System.Text.RegularExpressions;

namespace HikingGuide.Infrastructure.TrackFiles;

internal static partial class KmlDescriptionSanitizer
{
    private static readonly Regex CachedImageTag = new("<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static Regex ImageTag() => CachedImageTag;
    private static readonly Regex CachedImageLink = new("<a\\b[^>]*>\\s*<img\\b[^>]*>\\s*</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static Regex ImageLink() => CachedImageLink;
    private static readonly Regex CachedEmptyMediaDiv = new("<div\\b[^>]*>\\s*</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static Regex EmptyMediaDiv() => CachedEmptyMediaDiv;
    private static readonly Regex CachedBreakTag = new("<br\\s*/?>", RegexOptions.IgnoreCase);
    private static Regex BreakTag() => CachedBreakTag;
    private static readonly Regex CachedAnchorTag = new("</?a\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static Regex AnchorTag() => CachedAnchorTag;
    private static readonly Regex CachedDivTag = new("</?div\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static Regex DivTag() => CachedDivTag;
    private static readonly Regex CachedExcessBlankLines = new("(?:\\r?\\n[ \\t]*){2,}");
    private static Regex ExcessBlankLines() => CachedExcessBlankLines;

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
