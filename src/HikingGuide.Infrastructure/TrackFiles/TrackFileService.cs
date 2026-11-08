using System.Diagnostics;
using System.Xml;
using HikingGuide.Application.Logging;
using HikingGuide.Application.TrackFiles;

namespace HikingGuide.Infrastructure.TrackFiles;

public sealed class TrackFileService : ITrackFileService
{
    private readonly IReadOnlyDictionary<TrackFileFormat, ITrackImporter> _importers;
    private readonly IReadOnlyDictionary<TrackFileFormat, ITrackExporter> _exporters;
    private readonly ILoggingService _logging;
    private readonly TrackFileFormatDetector _detector = new();

    public TrackFileService(
        IEnumerable<ITrackImporter> importers,
        IEnumerable<ITrackExporter> exporters,
        ILoggingService logging)
    {
        _importers = importers.ToDictionary(importer => importer.Format);
        _exporters = exporters.ToDictionary(exporter => exporter.Format);
        _logging = logging;
    }

    public async Task<TrackImportResult> ImportAsync(
        string filePath,
        TrackFileFormat? expectedFormat = null,
        IProgress<TrackOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var emptyResult = new TrackImportResult { SourceFile = fullPath };

        if (!File.Exists(fullPath))
        {
            emptyResult.Errors.Add(new ImportError("FileNotFound", "找不到指定的轨迹文件。"));
            return emptyResult;
        }

        try
        {
            progress?.Report(new TrackOperationProgress("正在识别轨迹文件格式……"));
            var detection = await _detector.DetectAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var format = detection.ContentFormat;

            if (format == TrackFileFormat.Unknown)
            {
                emptyResult.Errors.Add(new ImportError("UnsupportedFormat", "无法识别该文件的轨迹格式。"));
                return emptyResult;
            }

            if (detection.ExtensionFormat != TrackFileFormat.Unknown && detection.ExtensionFormat != format)
            {
                var expectedName = detection.ExtensionFormat.ToString().ToUpperInvariant();
                emptyResult.Errors.Add(new ImportError(
                    "FormatMismatch",
                    $"文件扩展名为 {expectedName}，但文件内容看起来不是有效的 {expectedName} 文件。"));
                return emptyResult;
            }

            if (expectedFormat.HasValue && expectedFormat.Value != TrackFileFormat.Unknown &&
                expectedFormat.Value != format)
            {
                var expectedName = expectedFormat.Value.ToString().ToUpperInvariant();
                emptyResult.Errors.Add(new ImportError(
                    "FormatMismatch",
                    $"所选导入格式为 {expectedName}，但文件内容看起来不是有效的 {expectedName} 文件。"));
                return emptyResult;
            }

            if (!_importers.TryGetValue(format, out var importer))
            {
                emptyResult.Errors.Add(new ImportError("UnsupportedFormat", "当前版本不支持该轨迹格式。"));
                return emptyResult;
            }

            var stopwatch = Stopwatch.StartNew();
            _logging.Information($"开始导入{format.ToString().ToUpperInvariant()}：{Path.GetFileName(fullPath)}");
            var result = await importer.ImportAsync(fullPath, progress, cancellationToken).ConfigureAwait(false);
            result.SourceFile = fullPath;
            result.SourceFormat = format;

            progress?.Report(new TrackOperationProgress("正在重新计算轨迹累计距离……"));
            foreach (var track in result.Tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                track.RecalculateDistances();
            }

            if (result.Tracks.Count == 0 && result.Waypoints.Count > 0)
            {
                result.Warnings.Add(new ImportWarning(
                    "NoTracks",
                    $"文件中没有找到轨迹，但成功读取了 {result.Waypoints.Count} 个标注点。"));
            }
            else if (result.Tracks.Count == 0)
            {
                result.Warnings.Add(new ImportWarning("NoTracks", "文件中没有找到轨迹。"));
            }

            _logging.Information($"读取轨迹：{result.Tracks.Count}条");
            _logging.Information($"读取轨迹点：{result.TrackPointCount}个");
            _logging.Information($"读取标注点：{result.Waypoints.Count}个");
            foreach (var warning in result.Warnings)
            {
                _logging.Warning($"{warning.Message}{(warning.Context is null ? string.Empty : $"（{warning.Context}）")}");
            }
            _logging.Information("重新计算轨迹累计距离完成");
            _logging.Information($"{format.ToString().ToUpperInvariant()}导入完成，用时：{stopwatch.Elapsed.TotalMilliseconds:F3} ms");
            return result;
        }
        catch (OperationCanceledException)
        {
            _logging.Warning("轨迹导入已取消");
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            _logging.Error(exception, "轨迹文件读取权限不足");
            emptyResult.Errors.Add(new ImportError("AccessDenied", "无法读取该文件，请检查文件权限。"));
        }
        catch (XmlException exception)
        {
            _logging.Error(exception, "轨迹XML解析失败");
            var message = exception.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
                ? "文件包含不允许的DTD或外部实体，已拒绝读取。"
                : "文件内容不完整或XML格式错误。";
            emptyResult.Errors.Add(new ImportError("InvalidXml", message));
        }
        catch (IOException exception)
        {
            _logging.Error(exception, "轨迹文件读取失败");
            emptyResult.Errors.Add(new ImportError("ReadFailed", "无法读取该文件，请检查文件是否被占用或文件权限。"));
        }
        catch (InvalidDataException exception)
        {
            _logging.Warning($"KMZ内容不安全或不完整：{exception.Message}");
            emptyResult.Errors.Add(new ImportError("InvalidKmz", exception.Message));
        }
        catch (Exception exception)
        {
            _logging.Error(exception, "轨迹导入发生未预期错误");
            emptyResult.Errors.Add(new ImportError("Unexpected", "导入轨迹时发生未预期错误，详细信息已经写入日志。"));
        }

        return emptyResult;
    }

    public async Task ExportAsync(
        TrackFileFormat format,
        TrackExportRequest request,
        string filePath,
        bool overwrite = false,
        IProgress<TrackOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!_exporters.TryGetValue(format, out var exporter))
        {
            throw new TrackExportException("当前版本不支持该导出格式。");
        }

        var fullPath = Path.GetFullPath(filePath);
        if (File.Exists(fullPath) && !overwrite)
        {
            throw new TrackExportException("目标文件已经存在，未获得覆盖确认。请选择其它文件名或明确确认覆盖。");
        }

        try
        {
            _logging.Information($"开始导出{format.ToString().ToUpperInvariant()}：{Path.GetFileName(fullPath)}");
            await exporter.ExportAsync(request, fullPath, overwrite, progress, cancellationToken)
                .ConfigureAwait(false);
            _logging.Information($"{format.ToString().ToUpperInvariant()}导出完成：{Path.GetFileName(fullPath)}");
        }
        catch (OperationCanceledException)
        {
            _logging.Warning("轨迹导出已取消");
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            _logging.Error(exception, "轨迹文件写入权限不足");
            throw new TrackExportException("无法写入该文件，请检查目标文件夹权限。", exception);
        }
        catch (IOException exception)
        {
            _logging.Error(exception, "轨迹文件写入失败");
            throw new TrackExportException("无法写入轨迹文件，请检查文件是否被占用。", exception);
        }
    }
}
