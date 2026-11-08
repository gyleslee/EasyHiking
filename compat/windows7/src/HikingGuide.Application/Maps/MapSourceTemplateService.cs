using System.Text.RegularExpressions;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

public sealed class MapSourceValidationResult
{
    public List<string> Errors { get; } = [];
    public bool IsValid => Errors.Count == 0;
}

public sealed partial class MapSourceTemplateService
{
    public string NormalizeForMapLibre(string urlTemplate) =>
        MapTileTemplateParser.Parse(urlTemplate).NormalizedTemplates[0];

    public string BuildTileUrl(string urlTemplate, int zoom, int x, int y) =>
        NormalizeForMapLibre(urlTemplate)
            .Replace("{z}", zoom.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

    public MapSourceValidationResult Validate(MapSourceDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new MapSourceValidationResult();
        if (source.Id == Guid.Empty) result.Errors.Add("图源 ID 不能为空。");
        if (string.IsNullOrWhiteSpace(source.Name)) result.Errors.Add("图源名称不能为空。");
        if (string.IsNullOrWhiteSpace(source.UrlTemplate)) result.Errors.Add("网址模板不能为空。");
        else
        {
            try { MapTileTemplateParser.Parse(source.UrlTemplate); }
            catch (ArgumentException error) { result.Errors.Add(error.Message); }
        }
        if (source.MinZoom is < 0 or > 24) result.Errors.Add("最小缩放必须在 0～24 之间。");
        if (source.MaxZoom is < 0 or > 24) result.Errors.Add("最大缩放必须在 0～24 之间。");
        if (source.MinZoom > source.MaxZoom) result.Errors.Add("最小缩放不能大于最大缩放。");
        if (!Enum.IsDefined(source.CoordinateSystem)) result.Errors.Add("图源坐标系无效，请选择WGS-84、GCJ-02或BD-09。");
        if (!double.IsFinite(source.Opacity) || source.Opacity is < 0 or > 1) result.Errors.Add("透明度必须在 0%～100% 之间。");
        if (source.EffectiveUsage is not (MapSourceUsage.Base or MapSourceUsage.Overlay or MapSourceUsage.Both)) result.Errors.Add("图源可用位置无效。");
        if (source.UserAgent?.Any(char.IsControl) == true || source.Referer?.Any(char.IsControl) == true || source.Attribution?.Any(c => char.IsControl(c) && c != '\t') == true) result.Errors.Add("请求设置或版权说明包含无效控制字符。");
        if (!string.IsNullOrWhiteSpace(source.Referer) && (!Uri.TryCreate(source.Referer, UriKind.Absolute, out var referer) || referer.Scheme is not ("http" or "https"))) result.Errors.Add("Referer必须是HTTP或HTTPS地址。");
        foreach (var header in source.Headers ?? [])
            if (string.IsNullOrWhiteSpace(header.Key) || !header.Key.All(c => System.LegacyCharacter.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)) ||
                header.Value is null || header.Value.Any(char.IsControl) || new[] {"Host","Content-Length","Connection","Transfer-Encoding"}.Contains(header.Key,StringComparer.OrdinalIgnoreCase)) result.Errors.Add("高级请求头包含无效字符或受限制的字段。");
        return result;
    }

    private string BuildValidationUrl(string template) => BuildTileUrl(template, 0, 0, 0);

    private static readonly Regex CachedZoomTokenRegex = new("\\{zoom\\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static Regex ZoomTokenRegex() => CachedZoomTokenRegex;
}
