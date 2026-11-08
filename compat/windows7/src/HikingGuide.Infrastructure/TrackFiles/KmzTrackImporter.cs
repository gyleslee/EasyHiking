using System.IO.Compression;
using System.Xml;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Application.Media;

namespace HikingGuide.Infrastructure.TrackFiles;

public sealed class KmzSafetyProfile
{
    public int MaximumEntries { get; init; } = 10000;
    public long MaximumSingleEntryBytes { get; init; } = 64L * 1024 * 1024;
    public long MaximumTotalExpandedBytes { get; init; } = 256L * 1024 * 1024;
    public long MaximumKmlBytes { get; init; } = 32L * 1024 * 1024;
    public double MaximumCompressionRatio { get; init; } = 1000;
    public int MaximumEntryPathLength { get; init; } = 512;
}

public sealed class KmzTrackImporter(KmzSafetyProfile? profile = null,
    IImageDecodeValidator? imageValidator = null) : ITrackImporter
{
    private readonly KmzSafetyProfile _profile = profile ?? new KmzSafetyProfile();
    private readonly KmlTrackImporter _kml = new(imageValidator);
    public TrackFileFormat Format => TrackFileFormat.Kmz;

    public async Task<TrackImportResult> ImportAsync(string filePath,
        IProgress<TrackOperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new TrackOperationProgress("正在安全扫描KMZ归档……"));
        using var archive = ZipFile.OpenRead(filePath);
        var entries = Scan(archive);
        var kmlEntries = entries.Where(pair => pair.Key.EndsWith(".kml", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (kmlEntries.Length == 0) throw new InvalidDataException("KMZ中没有可用的KML文件。");
        var entry = entries.TryGetValue("doc.kml", out var doc) ? doc :
            kmlEntries.Length == 1 ? kmlEntries[0].Value :
            throw new InvalidDataException("KMZ包含多个KML文件且没有根目录doc.kml，无法判断主文档。");
        var kmlPath = NormalizeEntryPath(entry.FullName);
        if (entry.Length > _profile.MaximumKmlBytes)
            throw new InvalidDataException("KMZ中的KML文档过大。");
        progress?.Report(new TrackOperationProgress("正在解析KMZ中的KML……"));
        await using var kmlStream = entry.Open();
        var document = await XmlSupport.LoadSecureDocumentAsync(kmlStream, cancellationToken).ConfigureAwait(false);
        var result = _kml.ImportDocument(document, filePath,
            (reference, token) => Task.FromResult(ResolveImage(entries, kmlPath, reference, token)), cancellationToken);
        result.SourceFormat = TrackFileFormat.Kmz;
        return result;
    }

    private Dictionary<string, ZipArchiveEntry> Scan(ZipArchive archive)
    {
        if (archive.Entries.Count > _profile.MaximumEntries)
            throw new InvalidDataException("KMZ条目数量超过安全限制。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = NormalizeEntryPath(entry.FullName);
            if (name.Length > _profile.MaximumEntryPathLength)
                throw new InvalidDataException("KMZ路径过长。");
            if (!entries.TryAdd(name, entry)) throw new InvalidDataException("KMZ包含重复路径。");
            if (entry.Length > _profile.MaximumSingleEntryBytes)
                throw new InvalidDataException("KMZ包含过大的条目。");
            total = checked(total + entry.Length);
            if (total > _profile.MaximumTotalExpandedBytes)
                throw new InvalidDataException("KMZ展开后的总大小超过安全限制。");
            if (entry.Length > 0 &&
                (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > _profile.MaximumCompressionRatio))
                throw new InvalidDataException("KMZ压缩比异常，已拒绝读取。");
        }
        return entries;
    }

    internal static string NormalizeEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.StartsWith('/') ||
            path.StartsWith('\\') || path.Contains(':') || path.Contains('\\'))
            throw new InvalidDataException("KMZ包含不安全的绝对路径或盘符路径。");
        var decoded = Uri.UnescapeDataString(path);
        if (decoded.StartsWith('/') || decoded.StartsWith('\\') || decoded.Contains(':') || decoded.Contains('\\'))
            throw new InvalidDataException("KMZ路径解码后不安全。");
        var parts = new List<string>();
        foreach (var part in decoded.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") throw new InvalidDataException("KMZ包含路径穿越条目。");
            parts.Add(part);
        }
        if (parts.Count == 0) throw new InvalidDataException("KMZ包含空路径条目。");
        return string.Join('/', parts);
    }

    private byte[]? ResolveImage(Dictionary<string, ZipArchiveEntry> entries, string kmlPath,
        string reference, CancellationToken cancellationToken)
    {
        string path;
        try
        {
            var decoded = Uri.UnescapeDataString(reference.Split('?', '#')[0]).Replace('\\', '/');
            if (decoded.StartsWith('/') || decoded.Contains(':')) return null;
            var basePath = kmlPath.Contains('/') ? kmlPath[..(kmlPath.LastIndexOf('/') + 1)] : "";
            path = NormalizeEntryPath(basePath + decoded);
        }
        catch (InvalidDataException) { return null; }
        if (!entries.TryGetValue(path, out var entry) || entry.Length > _profile.MaximumSingleEntryBytes)
            return null;
        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Length + count > _profile.MaximumSingleEntryBytes)
                throw new InvalidDataException("KMZ图片超过单文件安全限制。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
