using HikingGuide.Application.Maps;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Guides;

public sealed class ProjectImportBatch(ITrackFileService trackFiles)
{
    public async Task<TrackImportResult> ReadAsync(IReadOnlyList<string> paths, HikingProject project,
        IProgress<TrackOperationProgress>? progress = null, CancellationToken cancellationToken = default,
        TrackFileFormat? expectedFormat = null)
    {
        if (paths.Count == 0) throw new ArgumentException("请选择至少一个轨迹文件。", nameof(paths));
        var merged = new TrackImportResult { SourceFormat = TrackFileFormat.Unknown };
        var colors = project.Tracks.ToList();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new TrackOperationProgress($"正在导入：{Path.GetFileName(path)}"));
            var part = await trackFiles.ImportAsync(path, expectedFormat, progress, cancellationToken).ConfigureAwait(false);
            if (!part.IsSuccess)
            {
                foreach (var error in part.Errors)
                    merged.Errors.Add(new ImportError(error.Code, $"{Path.GetFileName(path)}：{error.Message}"));
                continue;
            }
            foreach (var waypoint in part.Waypoints.Where(item => item.AssociatedTrackId is null &&
                         item.OriginalAttributes.ContainsKey("hg:relatedTrackKey")))
            {
                var key = waypoint.OriginalAttributes["hg:relatedTrackKey"];
                var matches = part.Tracks.Where(track => track.Metadata.GetValueOrDefault("hg:trackKey") == key).ToArray();
                if (matches.Length == 1) waypoint.AssociatedTrackId = matches[0].Id;
            }
            // 工作区将一个KMZ文档视为一条完整路线：内部轨迹保留为可编辑拆分分段。
            // 底层解析器仍原样返回几何，避免改变其它调用者的文件协议语义。
            if (part.SourceFormat == TrackFileFormat.Kmz && part.Tracks.Count > 1)
            {
                var originals = part.Tracks.ToArray();
                var grouped = new Track
                {
                    Name = Path.GetFileNameWithoutExtension(path), SourceFile = path,
                    SourceFormat = originals[0].SourceFormat,
                    Description = originals[0].Description,
                    Metadata = new Dictionary<string, string?> { ["hg:importGrouping"] = "KMZ内部轨迹作为拆分分段" }
                };
                var number = 0;
                foreach (var original in originals)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = Guid.NewGuid();
                    foreach (var segment in original.Segments)
                    {
                        segment.SplitSectionId = id;
                        segment.SplitSectionName = string.IsNullOrWhiteSpace(original.Name) ? $"分段 {number + 1}" : original.Name;
                        segment.OriginalAttributes["hg:sourceTrackName"] = original.Name;
                        grouped.Segments.Add(segment);
                    }
                    number++;
                }
                grouped.RecalculateDistances();
                foreach (var waypoint in part.Waypoints) waypoint.AssociatedTrackId = grouped.Id;
                part.Tracks.Clear(); part.Tracks.Add(grouped);
            }
            if (part.Tracks.Count == 1)
            {
                var track = part.Tracks[0];
                var fileName = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    track.Metadata["hg:sourceTrackName"] = track.Name;
                    track.Name = fileName;
                }
                foreach (var waypoint in part.Waypoints)
                    if (waypoint.AssociatedTrackId is null)
                        waypoint.AssociatedTrackId = track.Id;
            }
            foreach (var track in part.Tracks)
            {
                track.DisplayColor = TrackDisplayColor.NextUnique(colors);
                colors.Add(track);
                merged.Tracks.Add(track);
            }
            merged.Waypoints.AddRange(part.Waypoints);
            merged.MediaBlobs.AddRange(part.MediaBlobs);
            merged.MediaAttachments.AddRange(part.MediaAttachments);
            merged.ExternalMediaReferences.AddRange(part.ExternalMediaReferences);
            merged.Warnings.AddRange(part.Warnings);
        }
        merged.SourceFile = string.Join("; ", paths);
        return merged;
    }
}
