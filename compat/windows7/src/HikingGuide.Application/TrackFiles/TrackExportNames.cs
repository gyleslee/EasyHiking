using HikingGuide.Core.Models;

namespace HikingGuide.Application.TrackFiles;

public static class TrackExportNames
{
    /// <summary>单轨文档名必须与轨迹名一致，避免第三方采用项目名。</summary>
    public static string DocumentName(IReadOnlyList<Track> tracks, string? fallback) => tracks.Count == 1
        ? string.IsNullOrWhiteSpace(tracks[0].Name) ? "未命名轨迹" : tracks[0].Name!
        : tracks.Count > 1 ? string.Join("、", tracks.Select(t=>string.IsNullOrWhiteSpace(t.Name)?"未命名轨迹":t.Name))
        : fallback ?? "标注导出";

    public static string FileName(IReadOnlyList<Track> tracks, string? fallback) => tracks.Count <= 1
        ? DocumentName(tracks,fallback) : $"{DocumentName([tracks[0]],null)}等{tracks.Count}条轨迹";
}
