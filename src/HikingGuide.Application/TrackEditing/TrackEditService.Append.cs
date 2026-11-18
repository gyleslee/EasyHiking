using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.TrackEditing;

public sealed partial class TrackEditService
{
    public TrackEditPlan PrepareAppend(HikingProject project, Track track, Track extension,
        CancellationToken token=default)
    {
        if(!project.Tracks.Contains(track)||track.EndPoint is null||extension.StartPoint is null)
            throw new InvalidOperationException("请选择仍属于本项目的有效轨迹。");
        var parts=track.Segments.Select(CopySegment).ToList();
        var last=parts.Last(s=>!s.IsEmpty);
        var gap=GeoDistanceCalculator.Instance.CalculateDistance(last.EndPoint!.Coordinate,extension.StartPoint.Coordinate);
        // 保留原终点；BRouter路网入口不一致时明确连接，不移动原轨迹任何点。
        foreach(var part in extension.Segments)
        {
            token.ThrowIfCancellationRequested();
            var points=part.Points;
            last.Points.AddRange(points.Skip(last.EndPoint!.Coordinate==part.StartPoint!.Coordinate?1:0));
        }
        return BuildPlan(project,track,"继续规划路线",parts,NoMappings,NoRemovedSegments,false,true,token,
            gap>0.01?[$"原终点至规划路网起点新增直线接入：{gap:F1} 米。"]:[]);
    }
}
