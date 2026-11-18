using HikingGuide.Application.Guides;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.TrackEditing;

public sealed record TrackGeometrySelection(Guid TrackId, IReadOnlySet<Guid>? SectionIds = null);

/// <summary>整轨与分段混选先统一校验；失败回滚已应用步骤，撤销逆序还原节点及人工内容。</summary>
public sealed class TrackGeometryBatchPlan
{
    private readonly HikingProject _project;
    private readonly List<(Action Apply, Action Undo)> _steps;
    private bool _applied;
    public IReadOnlySet<Guid> RelatedMarkerIds { get; private init; } = new HashSet<Guid>();
    private TrackGeometryBatchPlan(HikingProject project, List<(Action, Action)> steps) { _project = project; _steps = steps; }

    public static TrackGeometryBatchPlan Prepare(HikingProject project,
        IReadOnlyList<TrackGeometrySelection> selections, bool delete, CancellationToken token = default)
    {
        if (selections.Count == 0) throw new InvalidOperationException("请先选择轨迹或拆分分段。");
        var normalized = selections.GroupBy(s => s.TrackId).Select(g => new TrackGeometrySelection(g.Key,
            g.Any(s => s.SectionIds is null) ? null : g.SelectMany(s => s.SectionIds!).ToHashSet())).ToArray();
        var plans = new List<TrackEditPlan>(); var removals = new List<Track>();
        var service = new TrackEditService();
        foreach (var item in normalized)
        {
            token.ThrowIfCancellationRequested();
            var track = project.Tracks.SingleOrDefault(t => t.Id == item.TrackId) ?? throw new InvalidOperationException("所选轨迹已不存在，请重新选择。");
            var sections = track.GetSplitSections();
            if (item.SectionIds is { } ids && (ids.Count == 0 || ids.Any(id => sections.All(s => s.Id != id))))
                throw new InvalidOperationException("所选分段已失效，请重新选择。");
            if (delete && (item.SectionIds is null || item.SectionIds.Count == sections.Count)) removals.Add(track);
            else plans.Add(delete ? service.PrepareDeleteSplitSections(project, track.Id, item.SectionIds!, token)
                : item.SectionIds is null ? service.PrepareReverseTrack(project, track.Id, token)
                : service.PrepareReverseSections(project, track.Id, item.SectionIds, token));
        }
        var steps = plans.OrderBy(p => p.TrackId == project.MainGuideTrackId).Select(p => ((Action)p.Apply, (Action)p.Undo)).ToList();
        foreach (var track in removals)
        {
            TrackDeletionTransaction? transaction = null;
            steps.Add((() => { transaction = new TrackManagementService().PrepareDeletion(project, track.Id); transaction.Execute(); },
                () => transaction!.Undo()));
        }
        var markers = new HashSet<Guid>();
        if (delete)
        {
            foreach (var track in removals) markers.UnionWith(TrackRelatedMarkers.Find(project, track));
            foreach (var plan in plans) markers.UnionWith(TrackRelatedMarkers.Find(project, project.Tracks.Single(t => t.Id == plan.TrackId), plan.ChangeSet));
        }
        return new(project, steps) { RelatedMarkerIds = markers };
    }

    public void Apply()
    {
        if (_applied) throw new InvalidOperationException("该批操作已执行。");
        lock (_project)
        {
            var count = 0;
            try { foreach (var step in _steps) { step.Apply(); count++; } _applied = true; }
            catch { for (var i = count - 1; i >= 0; i--) _steps[i].Undo(); throw; }
        }
    }
    public void Undo()
    {
        if (!_applied) throw new InvalidOperationException("该批操作尚未执行。");
        lock (_project) { for (var i = _steps.Count - 1; i >= 0; i--) _steps[i].Undo(); _applied = false; }
    }
}
