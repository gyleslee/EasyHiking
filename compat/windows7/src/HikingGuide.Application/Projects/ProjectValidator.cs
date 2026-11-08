using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using HikingGuide.Core.Elevation;

namespace HikingGuide.Application.Projects;

public sealed class ProjectValidator
{
    private const double DistanceToleranceMeters = 0.05;

    public ProjectValidationResult Validate(HikingProject project, bool repairDistances = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        var result = new ProjectValidationResult();
        if (project.Id == Guid.Empty) result.Errors.Add("项目 ID 不能为空。");
        if (project.ProjectVersion != ProjectMigrationRegistry.CurrentProjectVersion)
            result.Errors.Add($"不支持的项目版本：{project.ProjectVersion}。");
        try { project.Settings.GuideSnap.Validate(); }
        catch (ArgumentOutOfRangeException) { result.Errors.Add("攻略节点吸附告警距离无效。"); }

        CheckUnique(project.Tracks.Select(item => item.Id), "轨迹", result);
        CheckUnique(project.Tracks.SelectMany(item => item.Segments).Select(item => item.Id), "轨迹段", result);
        CheckUnique(project.Tracks.SelectMany(item => item.Segments).SelectMany(item => item.Points)
            .Select(item => item.Id), "轨迹点", result);
        CheckUnique(project.Waypoints.Select(item => item.Id), "标注点", result);
        CheckUnique(project.GuideNodes.Select(item => item.Id), "攻略节点", result);
        CheckUnique(project.GuideSegments.Select(item => item.Id), "攻略分段", result);
        CheckUnique(project.RetiredGuideSegments.Select(item => item.Id), "历史攻略分段", result);
        if (project.GuideSegments.Select(item => item.Id).Intersect(project.RetiredGuideSegments.Select(item => item.Id)).Any())
            result.Errors.Add("有效与历史攻略分段 ID 不得重复。");
        CheckUnique(project.RouteControlPoints.Select(item => item.Id), "路线控制点", result);
        CheckUnique(project.MediaBlobs.Select(item => item.Id), "媒体原图", result);
        CheckUnique(project.MediaAttachments.Select(item => item.Id), "媒体附件", result);
        CheckUnique(project.ExternalMediaReferences.Select(item => item.Id), "外部图片引用", result);
        CheckUnique(project.ElevationAnalyses.Select(item => item.Id), "高程分析结果", result);
        foreach (var analysis in project.ElevationAnalyses)
        {
            if (analysis.ProjectId != project.Id || analysis.Series is null ||
                analysis.Source is null || analysis.Profile is null || analysis.Summary is null ||
                analysis.GeometryFingerprint?.Length != 64 ||
                string.IsNullOrWhiteSpace(analysis.Source.SourceFingerprint))
            {
                result.Errors.Add("高程分析结果的归属、来源或结构无效。");
                continue;
            }
            try { analysis.Profile.Validate(); }
            catch (ArgumentOutOfRangeException) { result.Errors.Add("高程分析参数无效。"); }
            if (analysis.Source.SourceType == ElevationSourceType.Dem &&
                analysis.Source.DemDatabaseId is null)
                result.Errors.Add("DEM分析结果缺少数据库ID。");
            if (analysis.Series.Any(point => !double.IsFinite(point.DistanceMeters) ||
                point.DistanceMeters < 0 ||
                point.RawElevationMeters is double raw && !double.IsFinite(raw) ||
                point.ProcessedElevationMeters is double processed && !double.IsFinite(processed)))
                result.Errors.Add("高程分析序列包含无效数值。");
            if (analysis.Series.Zip(analysis.Series.Skip(1)).Any(pair =>
                pair.Second.DistanceMeters + 1e-6 < pair.First.DistanceMeters ||
                pair.Second.PartIndex < pair.First.PartIndex))
                result.Errors.Add("高程分析序列里程轴无效。");
        }
        var waypointIds = project.Waypoints.Select(item => item.Id).ToHashSet();
        var guideIds = project.GuideNodes.Select(item => item.Id).ToHashSet();
        var blobIds = project.MediaBlobs.Select(item => item.Id).ToHashSet();
        foreach (var blob in project.MediaBlobs)
        {
            if (blob.Id == Guid.Empty || blob.StoredPath != $"media/originals/{blob.Id:D}{blob.FileExtension}" ||
                blob.FileExtension is not (".jpg" or ".png" or ".webp") ||
                blob.MimeType is not ("image/jpeg" or "image/png" or "image/webp") ||
                blob.ByteLength <= 0 || blob.Width <= 0 || blob.Height <= 0 ||
                blob.Sha256.Length != 64 || !blob.Sha256.All(Uri.IsHexDigit))
                result.Errors.Add("项目图片原图元数据无效。");
            if (blob.PendingBytes is { } bytes &&
                (bytes.LongLength != blob.ByteLength ||
                 !Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(bytes))
                     .Equals(blob.Sha256, StringComparison.OrdinalIgnoreCase)))
                result.Errors.Add("项目图片原图内容校验失败。");
        }
        foreach (var attachment in project.MediaAttachments)
        {
            if (attachment.Id == Guid.Empty || !blobIds.Contains(attachment.MediaBlobId) ||
                attachment.MediaType != "Image" || attachment.DisplayOrder < 0 ||
                !ValidOwner(attachment.OwnerType, attachment.OwnerId, waypointIds, guideIds))
                result.Errors.Add("项目媒体附件引用无效。");
        }
        foreach (var reference in project.ExternalMediaReferences)
        {
            if (reference.Id == Guid.Empty || reference.DisplayOrder < 0 ||
                !ValidOwner(reference.OwnerType, reference.OwnerId, waypointIds, guideIds) ||
                !Uri.TryCreate(reference.Url, UriKind.Absolute, out var url) ||
                url.Scheme is not ("http" or "https"))
                result.Errors.Add("项目外部图片 URL 或归属无效。");
        }

        if (project.ActiveTrackId.HasValue && project.Tracks.All(item => item.Id != project.ActiveTrackId.Value))
            result.Errors.Add("当前工作轨迹引用了项目中不存在的轨迹。");

        var trackIds = project.Tracks.Select(track => track.Id).ToHashSet();
        var fingerprints = new TrackSnapService();
        var trackFingerprints = project.Tracks.ToDictionary(track => track.Id,
            track => fingerprints.GetGeometryFingerprint(track));
        if (project.MainGuideTrackId is Guid mainId && !trackIds.Contains(mainId))
            result.Errors.Add("主攻略轨迹引用了项目中不存在的轨迹。");
        foreach (var node in project.GuideNodes)
        {
            if (node.SourceTrackId is Guid sourceId && !trackIds.Contains(sourceId))
                result.Errors.Add($"攻略节点“{node.Name ?? node.Id.ToString()}”的来源轨迹不存在。");
            if (!node.IsOrphaned && node.TrackId != project.MainGuideTrackId)
                result.Errors.Add($"攻略节点“{node.Name ?? node.Id.ToString()}”不属于主攻略轨迹。");
            if (node.Type == WaypointType.Custom && string.IsNullOrWhiteSpace(node.CustomTypeName))
                result.Warnings.Add($"攻略节点“{node.Name ?? node.Id.ToString()}”的自定义类型名称为空。");
            if (node.TrackId.HasValue && !trackIds.Contains(node.TrackId.Value))
                result.Warnings.Add($"攻略节点“{node.Name ?? node.Id.ToString()}”关联的轨迹不存在，需重新指定。 ");
            if (node.IsOrphaned)
                result.Warnings.Add($"攻略节点“{node.Name ?? node.Id.ToString()}”已失去有效轨迹位置。");
            if (node.DistanceToTrack > project.Settings.GuideSnap.AutoSnapWarningDistanceMeters)
                result.Warnings.Add($"攻略节点“{node.Name ?? node.Id.ToString()}”距轨迹超过 {project.Settings.GuideSnap.AutoSnapWarningDistanceMeters:F0} 米。");
            if (!node.IsOrphaned && node.ExactPosition is { } position &&
                (!double.IsFinite(position.Fraction) || position.Fraction is < 0 or > 1 ||
                 !double.IsFinite(position.DistanceFromTrackStart) || position.DistanceFromTrackStart < 0 ||
                 position.StartPointIndex < 0 || position.EndPointIndex < position.StartPointIndex))
                result.Errors.Add($"攻略节点“{node.Name ?? node.Id.ToString()}”的轨迹位置无效。");
            if (!node.IsOrphaned && node.ExactPosition is { } exact)
            {
                var owner = project.Tracks.FirstOrDefault(track => track.Id == node.TrackId);
                if (owner is null || exact.TrackId != owner.Id || exact.SegmentIndex < 0 ||
                    exact.SegmentIndex >= owner.Segments.Count ||
                    owner.Segments[exact.SegmentIndex].Id != exact.TrackSegmentId ||
                    exact.EndPointIndex >= owner.Segments[exact.SegmentIndex].Points.Count)
                    result.Errors.Add($"攻略节点“{node.Name ?? node.Id.ToString()}”的轨迹线段引用无效。");
                else if (exact.TrackGeometryFingerprint != trackFingerprints[owner.Id] ||
                    node.TrackGeometryFingerprint != exact.TrackGeometryFingerprint)
                    result.Errors.Add($"攻略节点“{node.Name ?? node.Id.ToString()}”的轨迹几何指纹已失效。");
            }
            CheckCoordinate(node.OriginalCoordinate.Latitude, node.OriginalCoordinate.Longitude, "攻略节点", result);
            if (node.SnappedCoordinate is { } snapped)
                CheckCoordinate(snapped.Latitude, snapped.Longitude, "攻略节点吸附坐标", result);
        }

        foreach (var waypoint in project.Waypoints)
        {
            if (waypoint.AssociatedTrackId is Guid ownerId && !trackIds.Contains(ownerId))
                result.Errors.Add($"普通标注“{waypoint.Name ?? waypoint.Id.ToString()}”的所属轨迹不存在。");
            CheckCoordinate(waypoint.Latitude, waypoint.Longitude, "标注点", result);
            if (waypoint.Type == WaypointType.Custom && string.IsNullOrWhiteSpace(waypoint.CustomTypeName))
                result.Warnings.Add($"普通标注“{waypoint.Name ?? waypoint.Id.ToString()}”的自定义类型名称为空。");
        }
        foreach (var segment in project.GuideSegments)
        {
            if (segment.TrackId != project.MainGuideTrackId)
                result.Errors.Add("正式攻略分段不属于主攻略轨迹。");
            var start = project.GuideNodes.FirstOrDefault(node => node.Id == segment.StartNodeId);
            var end = project.GuideNodes.FirstOrDefault(node => node.Id == segment.EndNodeId);
            if (start is null || end is null)
                result.Errors.Add("攻略分段引用了不存在的攻略节点。");
            else if (segment.TrackId.HasValue && (start.TrackId != segment.TrackId || end.TrackId != segment.TrackId))
                result.Errors.Add("攻略分段与起终点所属轨迹不一致。");
            if (!double.IsFinite(segment.StartDistance) || !double.IsFinite(segment.EndDistance) ||
                segment.EndDistance < segment.StartDistance)
                result.Errors.Add("攻略分段的累计距离无效。");
            else if (segment.EndDistance == segment.StartDistance)
                result.Warnings.Add("存在零距离攻略分段，请检查重复节点。");
        }
        foreach (var group in project.GuideNodes.Where(node => !node.IsOrphaned && node.DistanceFromStart.HasValue)
                     .GroupBy(node => node.TrackId))
        {
            var ordered = group.OrderBy(node => node.DistanceFromStart).ToArray();
            for (var index = 1; index < ordered.Length; index++)
                if (Math.Abs(ordered[index].DistanceFromStart!.Value - ordered[index - 1].DistanceFromStart!.Value) < 0.01)
                    result.Warnings.Add("同一轨迹存在同位置攻略节点，可能产生零距离分段。");
            var starts = group.Count(node => node.Type == WaypointType.Start);
            var finishes = group.Count(node => node.Type == WaypointType.Finish);
            if (starts != 1) result.Warnings.Add($"轨迹攻略起点数量为 {starts}，可继续保存但导出前需检查。");
            if (finishes != 1) result.Warnings.Add($"轨迹攻略终点数量为 {finishes}，可继续保存但导出前需检查。");
        }
        foreach (var controlPoint in project.RouteControlPoints)
            CheckCoordinate(controlPoint.Coordinate.Latitude, controlPoint.Coordinate.Longitude, "路线控制点", result);

        foreach (var track in project.Tracks)
        {
            if (track.Id == Guid.Empty) result.Errors.Add("轨迹 ID 不能为空。");
            if (track.Segments is null)
            {
                result.Errors.Add("项目中的轨迹段结构不完整。");
                continue;
            }

            var distanceInvalid = false;
            var previous = double.NegativeInfinity;
            foreach (var segment in track.Segments)
            {
                if (segment is null || segment.Points is null)
                {
                    result.Errors.Add("项目中的轨迹数据不完整。");
                    continue;
                }
                if (track.GeometryRevision > 0 && segment.Points.Count < 2)
                    result.Errors.Add($"编辑后的轨迹“{track.Name ?? track.Id.ToString()}”包含少于两个点的轨迹段。");

                DateTimeOffset? previousTimestamp = null;
                var reverseTimeDetected = false;

                foreach (var point in segment.Points)
                {
                    CheckCoordinate(point.Latitude, point.Longitude, "轨迹点", result);
                    if (point.IsInsertedByEditor &&
                        (point.SourceElevation.HasValue || point.Timestamp.HasValue))
                        result.Errors.Add("编辑器新增的轨迹点不得伪造来源海拔或时间。");
                    if (previousTimestamp.HasValue && point.Timestamp.HasValue &&
                        point.Timestamp.Value < previousTimestamp.Value) reverseTimeDetected = true;
                    if (point.Timestamp.HasValue) previousTimestamp = point.Timestamp;
                    if (!double.IsFinite(point.DistanceFromStart) || point.DistanceFromStart < -DistanceToleranceMeters ||
                        point.DistanceFromStart + DistanceToleranceMeters < previous)
                        distanceInvalid = true;
                    previous = point.DistanceFromStart;
                }
                if (reverseTimeDetected)
                    result.Warnings.Add($"轨迹“{track.Name ?? track.Id.ToString()}”的轨迹段存在非递增来源时间，请检查轨迹方向。");
            }

            if (!distanceInvalid && track.GetPointCount() > 0)
            {
                var cloneDistances = track.Segments.SelectMany(s => s.Points).Select(p => p.DistanceFromStart).ToArray();
                var points = track.Segments.SelectMany(s => s.Points).ToArray();
                track.RecalculateDistances();
                var actual = points.Select(p => p.DistanceFromStart).ToArray();
                distanceInvalid = cloneDistances.Zip(actual).Any(pair => Math.Abs(pair.First - pair.Second) > DistanceToleranceMeters);
                for (var index = 0; index < actual.Length; index++)
                    points[index].DistanceFromStart = cloneDistances[index];
            }

            if (distanceInvalid)
            {
                if (repairDistances)
                {
                    track.RecalculateDistances();
                    result.Warnings.Add($"轨迹“{track.Name ?? track.Id.ToString()}”的累计距离已重新计算。");
                }
                else result.Errors.Add($"轨迹“{track.Name ?? track.Id.ToString()}”的累计距离不一致。");
            }
        }

        return result;
    }

    private static void CheckUnique(IEnumerable<Guid> ids, string label, ProjectValidationResult result)
    {
        var list = ids.ToList();
        if (list.Any(id => id == Guid.Empty)) result.Errors.Add($"{label} ID 不能为空。");
        if (list.Count != list.Distinct().Count()) result.Errors.Add($"{label} ID 必须唯一。");
    }

    private static void CheckCoordinate(double latitude, double longitude, string label, ProjectValidationResult result)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90 ||
            !double.IsFinite(longitude) || longitude is < -180 or > 180)
            result.Errors.Add($"{label}包含无效坐标（纬度应为 -90～90，经度应为 -180～180）。");
    }

    private static bool ValidOwner(MediaOwnerType type, Guid id, HashSet<Guid> waypointIds, HashSet<Guid> guideIds) =>
        type switch
        {
            MediaOwnerType.Waypoint => waypointIds.Contains(id),
            MediaOwnerType.GuideNode => guideIds.Contains(id),
            _ => false
        };
}
