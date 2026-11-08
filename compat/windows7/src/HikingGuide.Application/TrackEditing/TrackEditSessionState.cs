using HikingGuide.Core.Models;

namespace HikingGuide.Application.TrackEditing;

/// <summary>
/// 项目级轨迹编辑模式与当前编辑目标。切换目标或重建地图上下文时轮换会话，
/// 使已经离开的轨迹、旧几何及旧拖动消息无法提交到当前项目。
/// 此状态不修改轨迹、攻略内容或项目脏状态。
/// </summary>
public sealed class TrackEditSessionState
{
    public HikingProject? Project { get; private set; }
    public Track? Track { get; private set; }
    public Guid? SessionId { get; private set; }
    public bool IsActive => Project is not null;

    public void Begin(HikingProject project, Track track)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(track);
        if (!project.Tracks.Contains(track))
            throw new ArgumentException("编辑轨迹必须属于当前项目。", nameof(track));
        Project = project;
        Track = track;
        SessionId = Guid.NewGuid();
    }

    public bool SelectTarget(HikingProject project, Track track, bool renewSession = false)
    {
        if (!ReferenceEquals(Project, project) || !project.Tracks.Contains(track)) return false;
        if (!renewSession && ReferenceEquals(Track, track) && SessionId.HasValue) return true;
        Track = track;
        SessionId = Guid.NewGuid();
        return true;
    }

    public void End()
    {
        Project = null;
        Track = null;
        SessionId = null;
    }

    public bool IsCurrent(HikingProject? currentProject, Guid? projectId,
        Guid? sessionId, Guid? trackId, long geometryRevision) =>
        ReferenceEquals(Project, currentProject) && Project is not null &&
        Project.Id == projectId && SessionId.HasValue && SessionId == sessionId &&
        Track is not null && Project.Tracks.Contains(Track) && Track.Id == trackId &&
        Track.GeometryRevision == geometryRevision;

    /// <summary>只有当前有效会话的点选才允许切换到同项目另一条真实轨迹。</summary>
    public Track? ResolveSelectionTarget(HikingProject? currentProject, Guid? projectId,
        Guid? sessionId, Guid? trackId, long geometryRevision)
    {
        if (!ReferenceEquals(Project, currentProject) || Project is null ||
            Project.Id != projectId || !SessionId.HasValue || SessionId != sessionId)
            return null;
        return Project.Tracks.FirstOrDefault(track => track.Id == trackId &&
            track.GeometryRevision == geometryRevision);
    }
}
