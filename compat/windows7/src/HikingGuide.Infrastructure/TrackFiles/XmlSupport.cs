using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace HikingGuide.Infrastructure.TrackFiles;

internal static class XmlSupport
{
    public static async Task<XDocument> LoadSecureDocumentAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = XmlReader.Create(stream, CreateSecureSettings(async: true));
        return await XDocument.LoadAsync(
            reader,
            LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<XDocument> LoadSecureDocumentAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(stream, CreateSecureSettings(async: true));
        return await XDocument.LoadAsync(reader,
            LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo,
            cancellationToken).ConfigureAwait(false);
    }

    public static XmlReaderSettings CreateSecureSettings(bool async) => new()
    {
        Async = async,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = false,
        IgnoreProcessingInstructions = false,
        MaxCharactersFromEntities = 0
    };

    public static XElement? Child(XContainer parent, string localName) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    public static IEnumerable<XElement> Children(XContainer parent, string localName) =>
        parent.Elements().Where(element => element.Name.LocalName == localName);

    public static string? ChildValue(XContainer parent, string localName)
    {
        var value = Child(parent, localName)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static string? ReadRichContent(XContainer parent, string localName)
    {
        var element = Child(parent, localName);
        if (element is null)
        {
            return null;
        }

        return string.Concat(element.Nodes().Select(node => node switch
        {
            XCData cdata => cdata.Value,
            XText text => text.Value,
            _ => node.ToString(SaveOptions.DisableFormatting)
        }));
    }

    public static bool TryParseDouble(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value);

    public static bool TryParseTimestamp(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out value);

    public static void PreserveElement(
        IDictionary<string, string?> attributes,
        string key,
        XElement? element)
    {
        if (element is null)
        {
            return;
        }

        var uniqueKey = key;
        var suffix = 2;
        while (attributes.ContainsKey(uniqueKey))
        {
            uniqueKey = $"{key}:{suffix++}";
        }

        attributes[uniqueKey] = element.ToString(SaveOptions.DisableFormatting);
    }

    public static void PreserveAttributes(
        XElement element,
        IDictionary<string, string?> attributes,
        string keyPrefix,
        params string[] excludedLocalNames)
    {
        var excluded = new HashSet<string>(excludedLocalNames, StringComparer.Ordinal);
        foreach (var attribute in element.Attributes().Where(attribute =>
                     !attribute.IsNamespaceDeclaration && !excluded.Contains(attribute.Name.LocalName)))
        {
            attributes[$"{keyPrefix}{attribute.Name}"] = attribute.Value;
        }
    }

    public static void ApplyPreservedAttributes(
        XElement element,
        IReadOnlyDictionary<string, string?> attributes,
        string keyPrefix)
    {
        foreach (var pair in attributes.Where(pair =>
                     pair.Key.StartsWith(keyPrefix, StringComparison.Ordinal) && pair.Value is not null))
        {
            var xNameText = pair.Key[keyPrefix.Length..];
            element.SetAttributeValue(XName.Get(xNameText), pair.Value);
        }
    }
}
