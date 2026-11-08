using System.Xml;
using System.IO.Compression;
using HikingGuide.Application.TrackFiles;

namespace HikingGuide.Infrastructure.TrackFiles;

internal sealed class TrackFileFormatDetector
{
    public async Task<FileFormatDetection> DetectAsync(string filePath, CancellationToken cancellationToken)
    {
        var extensionFormat = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".gpx" => TrackFileFormat.Gpx,
            ".kml" => TrackFileFormat.Kml,
            ".kmz" => TrackFileFormat.Kmz,
            _ => TrackFileFormat.Unknown
        };

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var signature = new byte[4];
        if (await stream.ReadAsync(signature, cancellationToken) == 4 &&
            signature[0] == 0x50 && signature[1] == 0x4b)
        {
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                return new FileFormatDetection(extensionFormat,
                    zip.Entries.Any(entry => entry.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
                        ? TrackFileFormat.Kmz : TrackFileFormat.Unknown);
            }
            catch (InvalidDataException) { return new FileFormatDetection(extensionFormat, TrackFileFormat.Unknown); }
        }
        stream.Position = 0;
        using var reader = XmlReader.Create(stream, XmlSupport.CreateSecureSettings(async: true));
        await reader.MoveToContentAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var contentFormat = reader.LocalName switch
        {
            "gpx" => TrackFileFormat.Gpx,
            "kml" => TrackFileFormat.Kml,
            _ => TrackFileFormat.Unknown
        };

        return new FileFormatDetection(extensionFormat, contentFormat);
    }
}

internal sealed record FileFormatDetection(
    TrackFileFormat ExtensionFormat,
    TrackFileFormat ContentFormat);
