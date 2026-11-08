using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Services;
using OSGeo.GDAL;
using OSGeo.OSR;

namespace HikingGuide.Infrastructure.Elevation;

public sealed partial class DemDatabaseScanner
{
    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".tif", ".tiff", ".hgt" };

    [GeneratedRegex("^[NS](?:[0-8][0-9]|90)[EW](?:0[0-9][0-9]|1[0-7][0-9]|180)\\.hgt$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HgtName();

    public Task<DemScanResult> ScanAsync(DemDatabase database,
        IProgress<DemScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(database, progress, cancellationToken), cancellationToken);

    private static DemScanResult Scan(DemDatabase database,
        IProgress<DemScanProgress>? progress, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(database.RootPath))
            return new(database with { Status = DemDatabaseStatus.Disconnected }, [],
                ["数据目录不可用；挂载配置已保留。"]);
        DemGdalRuntime.EnsureReady();
        var entries = new List<RasterIndexEntry>();
        var warnings = new List<string>();
        var discovered = 0;
        var invalid = 0;
        foreach (var path in EnumerateDemFiles(database.RootPath, warnings, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            discovered++;
            try { entries.Add(ReadHeader(database.RootPath, path)); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                invalid++;
                warnings.Add($"无效DEM：{Path.GetRelativePath(database.RootPath, path)}：{error.Message}");
            }
            progress?.Report(new(discovered, entries.Count, invalid, Path.GetFileName(path)));
        }
        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        var fingerprint = Fingerprint(entries);
        var updated = database with
        {
            Status = entries.Count == 0 ? DemDatabaseStatus.Empty : DemDatabaseStatus.Ready,
            Fingerprint = fingerprint, ValidFileCount = entries.Count,
            InvalidFileCount = invalid, LastScannedAt = DateTimeOffset.UtcNow
        };
        return new(updated, entries, warnings);
    }

    private static IEnumerable<string> EnumerateDemFiles(string root,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var folders = new Stack<string>();
        folders.Push(Path.GetFullPath(root));
        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders.Pop();
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(folder);
                children = Directory.GetDirectories(folder);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"无法读取目录：{folder}：{error.Message}");
                continue;
            }
            foreach (var file in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                if (Extensions.Contains(Path.GetExtension(file))) yield return file;
            foreach (var child in children.OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    folders.Push(child);
                else warnings.Add($"已跳过目录链接：{child}");
            }
        }
    }

    private static RasterIndexEntry ReadHeader(string root, string path)
    {
        var extension = Path.GetExtension(path);
        var hgt = extension.Equals(".hgt", StringComparison.OrdinalIgnoreCase);
        if (hgt && !HgtName().IsMatch(Path.GetFileName(path)))
            throw new InvalidDataException("HGT文件名不能定位地理位置。");
        using var dataset = Gdal.Open(path, Access.GA_ReadOnly) ??
            throw new InvalidDataException("GDAL无法打开该栅格。");
        var driver = dataset.GetDriver()?.ShortName;
        if (hgt ? driver != "SRTMHGT" : driver != "GTiff")
            throw new InvalidDataException("文件内容与TIF/HGT格式不符。");
        if (dataset.RasterCount < 1 || dataset.RasterXSize < 2 || dataset.RasterYSize < 2)
            throw new InvalidDataException("栅格尺寸或波段无效。");
        var gt = new double[6];
        dataset.GetGeoTransform(gt);
        if (gt.All(value => value == 0))
            throw new InvalidDataException("缺少有效地理变换参数。");
        var wkt = dataset.GetProjectionRef();
        if (string.IsNullOrWhiteSpace(wkt) && hgt) wkt = "EPSG:4326";
        if (string.IsNullOrWhiteSpace(wkt))
            throw new InvalidDataException("缺少坐标参考系统；不能假定为EPSG:4326。");
        using var native = new SpatialReference(wkt);
        native.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        using var wgs84 = new SpatialReference("");
        wgs84.SetFromUserInput("EPSG:4326");
        wgs84.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        using var toWgs84 = new CoordinateTransformation(native, wgs84);
        GeoCoordinate Convert(double pixelX, double pixelY)
        {
            var x = gt[0] + pixelX * gt[1] + pixelY * gt[2];
            var y = gt[3] + pixelX * gt[4] + pixelY * gt[5];
            var transformed = new double[3];
            toWgs84.TransformPoint(transformed, x, y, 0);
            return new GeoCoordinate(transformed[1], transformed[0]);
        }
        var corners = new[]
        {
            Convert(0, 0), Convert(dataset.RasterXSize, 0),
            Convert(0, dataset.RasterYSize),
            Convert(dataset.RasterXSize, dataset.RasterYSize),
            Convert(dataset.RasterXSize / 2d, 0),
            Convert(dataset.RasterXSize / 2d, dataset.RasterYSize),
            Convert(0, dataset.RasterYSize / 2d),
            Convert(dataset.RasterXSize, dataset.RasterYSize / 2d)
        };
        var box = new GeoBoundingBox(corners.Min(point => point.Latitude),
            corners.Max(point => point.Latitude), corners.Min(point => point.Longitude),
            corners.Max(point => point.Longitude));
        var centerX = dataset.RasterXSize / 2d;
        var centerY = dataset.RasterYSize / 2d;
        var center = Convert(centerX, centerY);
        var resolution = Math.Min(
            GeoDistanceCalculator.Instance.CalculateDistance(center, Convert(centerX + 1, centerY)),
            GeoDistanceCalculator.Instance.CalculateDistance(center, Convert(centerX, centerY + 1)));
        if (!double.IsFinite(resolution) || resolution <= 0)
            throw new InvalidDataException("无法计算像元分辨率。");
        using var band = dataset.GetRasterBand(1);
        band.GetNoDataValue(out var noData, out var hasNoData);
        var unit = band.GetUnitType() ?? "";
        var info = new FileInfo(path);
        return new(path, Path.GetRelativePath(root, path),
            hgt ? DemRasterFormat.Hgt : DemRasterFormat.GeoTiff,
            box, wkt, dataset.RasterXSize, dataset.RasterYSize, gt,
            resolution, hasNoData != 0 ? noData : null, unit,
            info.LastWriteTimeUtc, info.Length);
    }

    private static string Fingerprint(IEnumerable<RasterIndexEntry> entries)
    {
        var content = string.Join('\n', entries.Select(entry => string.Join('|',
            entry.RelativePath.Replace('\\', '/').ToUpperInvariant(), entry.FileSize,
            entry.LastWriteTimeUtc.Ticks, entry.PixelWidth, entry.PixelHeight,
            entry.NativeCrs, string.Join(',', entry.GeoTransform.Select(value =>
                value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}
