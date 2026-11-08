using HikingGuide.Application.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.TrackEditing;

/// <summary>已预计算的可逆编辑；Apply/Undo 不重新执行吸附算法。</summary>
public sealed class TrackEditPlan(
    HikingProject project, Track track, long expectedRevision,
    IReadOnlyList<TrackSegment> beforeSegments, IReadOnlyList<TrackSegment> afterSegments,
    TrackGeometryChangeSet changeSet,
    IReadOnlyDictionary<Guid, GuideNodeGeometryState> beforeNodes,
    IReadOnlyDictionary<Guid, GuideNodeGeometryState> afterNodes,
    IReadOnlyList<GuideSegment> beforeGuideSegments,
    IReadOnlyList<GuideSegment> beforeRetiredSegments,
    IReadOnlyList<GuideSegment> afterGuideSegments,
    IReadOnlyList<GuideSegment> afterRetiredSegments,
    bool analysisWasStale, TrackEditImpact impact, IReadOnlyList<string> warnings,
    SplitSectionSequenceChange? splitNaming = null)
{
    public Guid ProjectId => project.Id;
    public Guid TrackId => track.Id;
    public string Operation => changeSet.Operation;
    public TrackGeometryChangeSet ChangeSet => changeSet;
    public TrackEditImpact Impact => impact;
    public IReadOnlyList<string> Warnings => warnings;
    public int PointsBefore => beforeSegments.Sum(segment => segment.Points.Count);
    public int PointsAfter => afterSegments.Sum(segment => segment.Points.Count);
    public int SegmentsBefore => beforeSegments.Count;
    public int SegmentsAfter => afterSegments.Count;

    public void Apply() => SetState(expectedRevision, expectedRevision + 1,
        changeSet.OldFingerprint, afterSegments, afterNodes, afterGuideSegments,
        afterRetiredSegments, true);

    public void Undo() => SetState(expectedRevision + 1, expectedRevision,
        changeSet.NewFingerprint, beforeSegments, beforeNodes, beforeGuideSegments,
        beforeRetiredSegments, analysisWasStale,true);

    private void SetState(long requiredRevision, long nextRevision, string requiredFingerprint,
        IReadOnlyList<TrackSegment> segments, IReadOnlyDictionary<Guid, GuideNodeGeometryState> nodes,
        IReadOnlyList<GuideSegment> active, IReadOnlyList<GuideSegment> retired, bool analysisStale,bool undo=false)
    {
        project.ExecuteAtomicUpdate(p =>
        {
            if (nextRevision > requiredRevision && p.MainGuideTrackId == track.Id &&
                (changeSet.Operation.Contains("合并") || changeSet.Operation.Contains("连接") || changeSet.Operation.Contains("拆分")))
                throw new InvalidOperationException("主攻略轨迹不可合并、连接或拆分，请先转为普通轨迹。");
            if (p.Id != ProjectId || !p.Tracks.Contains(track) ||
                track.GeometryRevision != requiredRevision ||
                new TrackSnapService().GetGeometryFingerprint(track) != requiredFingerprint)
                throw new InvalidOperationException("项目轨迹在后台计算期间发生变化，请重新执行编辑。");
            var originalSequence=track.Metadata.GetValueOrDefault(TrackSplitNaming.LastNumberKey);
            if(splitNaming is not null&&originalSequence!=(undo?splitNaming.After:splitNaming.Before))
                throw new InvalidOperationException("拆分分段编号在后台计算期间发生变化，请重新执行编辑。");
            var originalSegments = track.Segments.ToArray();
            var originalRevision = track.GeometryRevision;
            var originalNodes = p.GuideNodes.Where(node => nodes.ContainsKey(node.Id))
                .ToDictionary(node => node.Id, GuideNodeGeometryState.From);
            var originalActive = p.GuideSegments.ToArray();
            var originalRetired = p.RetiredGuideSegments.ToArray();
            var originalAnalysisStale = p.IsAnalysisResultStale;
            try
            {
                track.Segments.Clear();
                track.Segments.AddRange(segments);
                track.RecalculateDistances();
                track.RestoreGeometryRevision(nextRevision);
                if(splitNaming is not null)
                {
                    var value=undo?splitNaming.Before:splitNaming.After;
                    if(value is null)track.Metadata.Remove(TrackSplitNaming.LastNumberKey);
                    else track.Metadata[TrackSplitNaming.LastNumberKey]=value;
                }
                foreach (var node in p.GuideNodes)
                    if (nodes.TryGetValue(node.Id, out var state)) state.Apply(node);
                if (p.MainGuideTrackId == track.Id)
                {
                    p.GuideSegments.Clear();
                    foreach (var segment in active) p.GuideSegments.Add(segment);
                    p.RetiredGuideSegments.Clear();
                    foreach (var segment in retired) p.RetiredGuideSegments.Add(segment);
                }
                p.IsAnalysisResultStale = analysisStale;
                p.NotifyTrackChanged();
            }
            catch
            {
                track.Segments.Clear(); track.Segments.AddRange(originalSegments);
                track.RecalculateDistances();
                track.RestoreGeometryRevision(originalRevision);
                if(splitNaming is not null)
                {
                    if(originalSequence is null)track.Metadata.Remove(TrackSplitNaming.LastNumberKey);
                    else track.Metadata[TrackSplitNaming.LastNumberKey]=originalSequence;
                }
                foreach (var node in p.GuideNodes)
                    if (originalNodes.TryGetValue(node.Id, out var state)) state.Apply(node);
                p.GuideSegments.Clear();
                foreach (var segment in originalActive) p.GuideSegments.Add(segment);
                p.RetiredGuideSegments.Clear();
                foreach (var segment in originalRetired) p.RetiredGuideSegments.Add(segment);
                p.IsAnalysisResultStale = originalAnalysisStale;
                throw;
            }
        });
    }
}
