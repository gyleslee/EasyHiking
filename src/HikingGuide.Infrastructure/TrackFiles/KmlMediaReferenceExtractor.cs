using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HikingGuide.Infrastructure.TrackFiles;

internal sealed record KmlMediaReference(string Value, bool IsExternal, string Kind, string Field,
    int? DisplayOrder = null, string? Caption = null);

internal static partial class KmlMediaReferenceExtractor
{
    [GeneratedRegex("<img\\b[^>]*?\\bsrc\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImageTags();
    [GeneratedRegex("<a\\b[^>]*?\\bhref\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkTags();
    [GeneratedRegex("https?://[^\\s<>\"']+", RegexOptions.IgnoreCase)]
    private static partial Regex Urls();
    [GeneratedRegex("(^|[_:-])(photo|image|picture|media)(url|src|path)?($|[_:-])", RegexOptions.IgnoreCase)]
    private static partial Regex MediaFieldNames();

    public static IReadOnlyList<KmlMediaReference> Extract(XElement placemark)
    {
        var output = new List<KmlMediaReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string raw, string kind, string field, bool allowLocal, int? order = null, string? caption = null)
        {
            var value = WebUtility.HtmlDecode(raw).Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme is "http" or "https" && seen.Add("url:" + value))
                    output.Add(new KmlMediaReference(value, true, kind, field, order, caption));
                return;
            }
            if (allowLocal && IsImagePath(value) && seen.Add("path:" + value))
                output.Add(new KmlMediaReference(value, false, kind, field, order, caption));
        }

        foreach (var element in placemark.Descendants().Where(item => item.Name.LocalName is "Data" or "SimpleData"))
        {
            var name = element.Attribute("name")?.Value;
            if (name is null || !name.StartsWith("hg:externalMedia:", StringComparison.Ordinal)) continue;
            var orderText = name["hg:externalMedia:".Length..];
            var order = int.TryParse(orderText, out var parsed) && parsed >= 0 ? parsed : (int?)null;
            Add(XmlSupport.ChildValue(element, "value") ?? element.Value, "HikingGuide", name,
                false, order, element.Attribute("caption")?.Value);
        }
        var description = XmlSupport.ReadRichContent(placemark, "description") ?? "";
        var fromDescription = ImageTags().Matches(description).Cast<Match>()
            .Select(match => (match.Index, Value: match.Groups[1].Value, Kind: "HtmlImage", AllowLocal: true))
            .Concat(LinkTags().Matches(description).Cast<Match>()
                .Select(match => (match.Index, Value: match.Groups[1].Value, Kind: "HtmlLink", AllowLocal: true)))
            .Concat(Urls().Matches(description).Cast<Match>()
                .Select(match => (match.Index, Value: match.Value.TrimEnd('.', ',', ';', ')'), Kind: "PlainUrl", AllowLocal: false)))
            .OrderBy(item => item.Index);
        foreach (var item in fromDescription) Add(item.Value, item.Kind, "description", item.AllowLocal);
        foreach (var element in placemark.Descendants().Where(item => item.Name.LocalName is "Data" or "SimpleData"))
        {
            var name = element.Attribute("name")?.Value;
            if (name is null || !MediaFieldNames().IsMatch(name)) continue;
            var value = XmlSupport.ChildValue(element, "value") ?? element.Value;
            Add(value, "ExtendedData", name, true);
        }
        return output;
    }

    private static bool IsImagePath(string path)
    {
        if (path.Length is < 1 or > 512 || path.StartsWith("//") || path.StartsWith('\\')) return false;
        var withoutQuery = path.Split('?', '#')[0];
        return Path.GetExtension(withoutQuery).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp";
    }
}
