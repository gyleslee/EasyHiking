using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.TrackFiles;

/// <summary>连接成果的兼容性导出视图；不改写项目几何、统计或原始轨迹。</summary>
internal static class ConnectedTrackExportGeometry
{
    public static IReadOnlyList<TrackSegment> Segments(Track track)
    {
        var connected = track.Metadata.GetValueOrDefault("hg:editorOperation") == "直线连接轨迹" ||
            track.GetSplitSections().Count == 1 && track.Segments.Any(s =>
                s.OriginalAttributes.GetValueOrDefault("hg:editorProvenance") == "人工直线连接");
        if (!connected || track.Segments.Count < 2) return track.Segments;
        // 保留重复边界点及其海拔/时间信息；只消除文件几何分段，不删点。
        return [new TrackSegment { Name=track.Name, Points=track.Segments.SelectMany(s=>s.Points).ToList() }];
    }
}
