using System.Text.RegularExpressions;

namespace HikingGuide.Application.Maps;

public sealed record ParsedMapTileTemplate(string RawTemplate, IReadOnlyList<string> NormalizedTemplates, IReadOnlyList<string> ServerTokens);

/// <summary>唯一XYZ模板解析器。保留原模板；展开在C#执行，不把未解析变量发往地图。</summary>
public static partial class MapTileTemplateParser
{
    public static ParsedMapTileTemplate Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException("网址模板不能为空。");
        if (raw.Any(char.IsControl)) throw new ArgumentException("网址模板包含控制字符。");
        // {$s}不是通用服务器范围：Google mt使用0～3，高德等已有图源保持01～04。
        // 只匹配网址主机，不能让查询参数或相似域名改变规则；显式switch始终由用户指定。
        var serverSwitch = GoogleMtHostRegex().IsMatch(raw) ? "{switch:0,1,2,3}" : "{switch:01,02,03,04}";
        var expanded = raw.Replace("{$s}", serverSwitch, StringComparison.OrdinalIgnoreCase);
        var switches = SwitchRegex().Matches(expanded);
        if (switches.Count > 1) throw new ArgumentException("当前每个图源只支持一个{switch:...}服务器切换表达式。");
        var tokens = switches.Count == 0 ? new[] { "默认" } : switches[0].Groups[1].Value.Split(',').Select(t => t.Trim()).ToArray();
        if (switches.Count > 0)
        {
            if (tokens.Length < 2) throw new ArgumentException("服务器切换至少需要两个值。");
            if (tokens.Any(string.IsNullOrEmpty)) throw new ArgumentException("服务器切换列表包含空值。");
            if (tokens.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tokens.Length) throw new ArgumentException("服务器切换列表包含重复值。");
            if (tokens.Any(t => !TokenRegex().IsMatch(t))) throw new ArgumentException("服务器切换值只能包含字母、数字、-、_、.、~。");
        }
        var urls = tokens.Select(token => switches.Count == 0 ? expanded : expanded[..switches[0].Index] + token + expanded[(switches[0].Index + switches[0].Length)..])
            .Select(t => ZoomRegex().Replace(t, "{z}"))
            .Select(t => TileRegex().Replace(t, m => "{" + m.Groups[1].Value.ToLowerInvariant() + "}" )).ToArray();
        foreach (var url in urls)
        {
            if (!url.Contains("{x}") || !url.Contains("{y}") || !url.Contains("{z}")) throw new ArgumentException("网址模板必须包含 {z} 或 {zoom}、{x}、{y}。");
            var sample = BuildTileUrl(url, 1, 1, 1);
            if (sample.Contains('{') || sample.Contains('}')) throw new ArgumentException("网址模板含有不支持或格式错误的变量；不支持 {s}、{-y} 或 quadkey。");
            if (!Uri.TryCreate(sample, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("网址模板必须是有效的 HTTP 或 HTTPS 地址，且不能包含用户名密码。");
        }
        return new(raw, urls, tokens);
    }
    public static string BuildTileUrl(string normalizedTemplate, int zoom, int x, int y) => normalizedTemplate
        .Replace("{z}", zoom.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static readonly Regex CachedSwitchRegex = new(@"\{switch:([^{}]*)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static Regex SwitchRegex() => CachedSwitchRegex;
    private static readonly Regex CachedGoogleMtHostRegex = new(@"\Ahttps?://mt\{\$s\}\.google\.com(?=[:/?#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static Regex GoogleMtHostRegex() => CachedGoogleMtHostRegex;
    private static readonly Regex CachedTokenRegex = new(@"^[a-zA-Z0-9_.~-]+$");
    private static Regex TokenRegex() => CachedTokenRegex;
    private static readonly Regex CachedZoomRegex = new(@"\{zoom\}", RegexOptions.IgnoreCase);
    private static Regex ZoomRegex() => CachedZoomRegex;
    private static readonly Regex CachedTileRegex = new(@"\{([zxy])\}", RegexOptions.IgnoreCase);
    private static Regex TileRegex() => CachedTileRegex;
}
