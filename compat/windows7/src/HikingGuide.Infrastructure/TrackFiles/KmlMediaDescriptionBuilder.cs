using System.Net;
using System.Text;
using System.Xml.Linq;

namespace HikingGuide.Infrastructure.TrackFiles;

/// <summary>P8-2项目导出与收藏点导出共用的安全文本/媒体HTML构造。</summary>
internal static class KmlMediaDescriptionBuilder
{
    public static StringBuilder PlainText(string? text) => new(WebUtility.HtmlEncode(text ?? "").Replace("\r\n", "\n").Replace("\n", "<br/>"));
    public static void Image(StringBuilder html, string source) => html.Append("<br/><img src=\"").Append(WebUtility.HtmlEncode(source)).Append("\"/>");
    public static bool IsHttpUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    public static void Link(StringBuilder html, string? url)
    { if (IsHttpUrl(url)) html.Append("<br/>链接：<a href=\"").Append(WebUtility.HtmlEncode(url)).Append("\">打开链接</a>"); }
    public static XElement Description(XNamespace ns, StringBuilder html) => new(ns + "description", new XCData(html.ToString()));
}
