using System.Xml.Linq;

namespace HikingGuide.Infrastructure.TrackFiles;

internal static class XmlExporterSupport
{
    public static async Task SaveAtomicallyAsync(
        XDocument document,
        string filePath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new IOException("无法确定目标文件夹。");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"目标文件夹不存在：{directory}");
        }

        if (File.Exists(fullPath) && !overwrite)
        {
            throw new IOException("目标文件已经存在。");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await document.SaveAsync(stream, SaveOptions.DisableFormatting, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static XElement? ReadPreservedElement(
        IReadOnlyDictionary<string, string?> attributes,
        string key,
        string expectedLocalName)
    {
        if (!attributes.TryGetValue(key, out var rawXml) || string.IsNullOrWhiteSpace(rawXml))
        {
            return null;
        }

        var element = XElement.Parse(rawXml, LoadOptions.PreserveWhitespace);
        return element.Name.LocalName == expectedLocalName ? element : null;
    }

    public static IEnumerable<XElement> ReadPreservedElementsByPrefix(
        IReadOnlyDictionary<string, string?> attributes,
        string keyPrefix)
    {
        foreach (var pair in attributes.Where(pair =>
                     pair.Key.StartsWith(keyPrefix, StringComparison.Ordinal) &&
                     !string.IsNullOrWhiteSpace(pair.Value)))
        {
            yield return XElement.Parse(pair.Value!, LoadOptions.PreserveWhitespace);
        }
    }
}
