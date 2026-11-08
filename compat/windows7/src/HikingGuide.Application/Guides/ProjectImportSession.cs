using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.Guides;

public sealed class ImportWaypointSelection(Waypoint waypoint)
{
    private bool _import = true;
    private bool _isGuideNode;
    public Waypoint Waypoint { get; } = waypoint;
    public bool Import
    {
        get => _import;
        set { _import = value; if (!value) _isGuideNode = false; }
    }
    public bool IsGuideNode
    {
        get => _isGuideNode;
        set { _isGuideNode = value; if (value) _import = true; }
    }
    public TrackSnapResult? SnapPreview { get; set; }
    public Guid? RelatedTrackId { get; set; }
}

public sealed class ProjectImportSession
{
    public required string SourceFile { get; init; }
    public required TrackImportResult Parsed { get; init; }
    public HashSet<Guid> SelectedTrackIds { get; } = [];
    public Guid? SelectedGuideTrackId { get; set; }
    public List<ImportWaypointSelection> WaypointSelections { get; } = [];
    public List<string> Warnings { get; } = [];

    public static ProjectImportSession Create(TrackImportResult parsed, string sourceFile, HikingProject project)
    {
        var session = new ProjectImportSession { Parsed = parsed, SourceFile = sourceFile };
        foreach (var track in parsed.Tracks) session.SelectedTrackIds.Add(track.Id);
        // 保留项目已有主攻略；新导入轨迹不默认指定主攻略，必须由用户选择。
        session.SelectedGuideTrackId = project.MainGuideTrackId;
        foreach (var waypoint in parsed.Waypoints)
        {
            var choice = new ImportWaypointSelection(waypoint);
            if (waypoint.OriginalAttributes.TryGetValue("hg:isGuideNode", out var guideFlag) &&
                bool.TryParse(guideFlag, out var guide) && guide) choice.IsGuideNode = true;
            if (waypoint.AssociatedTrackId is Guid ownerId &&
                parsed.Tracks.Any(track => track.Id == ownerId))
                choice.RelatedTrackId = ownerId;
            if (choice.RelatedTrackId is null && waypoint.OriginalAttributes.TryGetValue("hg:relatedTrackKey", out var trackKey))
                choice.RelatedTrackId = parsed.Tracks.FirstOrDefault(track =>
                    track.Metadata.GetValueOrDefault("hg:trackKey") == trackKey)?.Id;
            session.WaypointSelections.Add(choice);
        }
        return session;
    }

    public void SetAllGuide(bool value)
    {
        foreach (var selection in WaypointSelections) selection.IsGuideNode = value;
    }
    public void InvertGuide()
    {
        foreach (var selection in WaypointSelections) selection.IsGuideNode = !selection.IsGuideNode;
    }
    public void SetAllImport(bool value)
    {
        foreach (var selection in WaypointSelections) selection.Import = value;
    }
    public void InvertImport()
    {
        foreach (var selection in WaypointSelections) selection.Import = !selection.Import;
    }
}

public sealed class ProjectImportService(GuideStructureService? guideService = null)
{
    private readonly GuideStructureService _guideService = guideService ?? new GuideStructureService();
    private readonly TrackSnapService _snapper = new();

    public void Preview(ProjectImportSession session, HikingProject project)
    {
        foreach (var selection in session.WaypointSelections)
        {
            var track = FindGuideTrack(session, project);
            selection.SnapPreview = track is null ? null : _snapper.Snap(track, selection.Waypoint.Coordinate);
        }
    }

    public ProjectImportPlan Prepare(ProjectImportSession session, HikingProject project)
    {
        if (session.SelectedGuideTrackId is Guid chosenNew &&
            session.Parsed.Tracks.Any(track => track.Id == chosenNew) &&
            !session.SelectedTrackIds.Contains(chosenNew))
            throw new InvalidOperationException("主攻略轨迹必须同时勾选导入。");
        var selectedTracks = session.Parsed.Tracks.Where(t => session.SelectedTrackIds.Contains(t.Id)).ToArray();
        if (session.WaypointSelections.Any(s => s.Import && s.RelatedTrackId is Guid owner &&
            session.Parsed.Tracks.Any(track => track.Id == owner) && !session.SelectedTrackIds.Contains(owner)))
            throw new InvalidOperationException("标注所归属的轨迹未勾选导入；请先导入该轨迹或修改标注归属。");
        if (selectedTracks.Any(t => project.Tracks.Any(existing => existing.Id == t.Id)))
            throw new InvalidOperationException("导入轨迹 ID 与项目中已有轨迹冲突。");
        var selectedTrack = FindGuideTrack(session, project);
        var guideSelections = session.WaypointSelections.Where(s => s.Import && s.IsGuideNode).ToArray();
        if (session.WaypointSelections.Any(s => s.Import && s.Waypoint.Type == WaypointType.Custom &&
                string.IsNullOrWhiteSpace(s.Waypoint.CustomTypeName)))
            throw new InvalidOperationException("自定义类型的标注必须填写类型名称。");
        if (guideSelections.Length > 0 && selectedTrack is null)
            throw new InvalidOperationException("请先选择攻略轨迹，再导入攻略节点。");
        if (project.MainGuideTrackId is Guid existingMain && selectedTrack?.Id != existingMain)
            throw new InvalidOperationException("项目已有主攻略轨迹；请通过轨迹管理明确更换，不可在导入时替换。");
        var nodes = new List<GuideNode>();
        foreach (var selection in guideSelections)
        {
            var guideTrack = selectedTrack;
            var node = selection.Waypoint.ToGuideNode();
            node.SourceTrackId = selection.RelatedTrackId is Guid source &&
                (session.SelectedTrackIds.Contains(source) || project.Tracks.Any(track => track.Id == source))
                ? source : null;
            node.SourceTrackName = node.SourceTrackId is Guid sourceId
                ? session.Parsed.Tracks.Concat(project.Tracks).FirstOrDefault(track => track.Id == sourceId)?.Name ?? "（未命名轨迹）" : null;
            node.StableOrder = project.GuideNodes.Count + nodes.Count;
            if (guideTrack is null)
            {
                node.TrackId = null;
                node.IsOrphaned = true;
            }
            else
            {
                var result = _snapper.Snap(guideTrack, selection.Waypoint.Coordinate);
                if (result.Position is null) throw new InvalidOperationException(result.FailureReason);
                node.TrackId = guideTrack.Id;
                _guideService.ApplyPosition(node, result.Position);
            }
            nodes.Add(node);
        }
        var importedWaypoints = session.WaypointSelections.Where(s => s.Import && !s.IsGuideNode)
            .Select(s =>
            {
                s.Waypoint.AssociatedTrackId = s.RelatedTrackId is Guid owner &&
                    (session.SelectedTrackIds.Contains(owner) || project.Tracks.Any(track => track.Id == owner)) ? owner : null;
                return s.Waypoint;
            }).ToArray();
        var selectedOwners = importedWaypoints.Select(item => item.Id).ToHashSet();
        var guideOwnerIds = guideSelections.Select(item => item.Waypoint.Id).ToHashSet();
        selectedOwners.UnionWith(guideOwnerIds);
        var selectedAttachments = session.Parsed.MediaAttachments.Where(item => selectedOwners.Contains(item.OwnerId)).ToArray();
        var selectedExternal = session.Parsed.ExternalMediaReferences.Where(item => selectedOwners.Contains(item.OwnerId)).ToArray();
        var importedBlobs = session.Parsed.MediaBlobs.Where(blob =>
            selectedAttachments.Any(item => item.MediaBlobId == blob.Id)).ToArray();
        var reused = importedBlobs.Where(blob => project.MediaBlobs.Any(item => item.Sha256 == blob.Sha256))
            .ToDictionary(blob => blob.Id, blob => project.MediaBlobs.First(item => item.Sha256 == blob.Sha256).Id);
        var blobsToAdd = importedBlobs.Where(blob => !reused.ContainsKey(blob.Id)).ToArray();
        var attachments = selectedAttachments.Select(item => new MediaAttachment
        {
            Id = item.Id, OwnerId = item.OwnerId,
            OwnerType = guideOwnerIds.Contains(item.OwnerId) ? MediaOwnerType.GuideNode : MediaOwnerType.Waypoint,
            MediaBlobId = reused.GetValueOrDefault(item.MediaBlobId, item.MediaBlobId),
            MediaType = item.MediaType, OriginalFileName = item.OriginalFileName,
            Caption = item.Caption, SourceKind = item.SourceKind,
            SourceReference = item.SourceReference, DisplayOrder = item.DisplayOrder, CreatedAt = item.CreatedAt
        }).ToArray();
        var external = selectedExternal.Select(item => new ExternalMediaReference
        {
            Id = item.Id, OwnerId = item.OwnerId,
            OwnerType = guideOwnerIds.Contains(item.OwnerId) ? MediaOwnerType.GuideNode : MediaOwnerType.Waypoint,
            Url = item.Url, DisplayName = item.DisplayName, Caption = item.Caption,
            SourceKind = item.SourceKind, SourceField = item.SourceField, DisplayOrder = item.DisplayOrder
        }).ToArray();
        return new ProjectImportPlan(selectedTracks, selectedTrack?.Id, importedWaypoints, nodes.ToArray(),
            blobsToAdd, attachments, external);
    }

    public void Commit(ProjectImportSession session, HikingProject project, bool confirmFarNodes) =>
        Apply(Prepare(session, project), project, confirmFarNodes);

    public void Apply(ProjectImportPlan plan, HikingProject project, bool confirmFarNodes)
    {
        var warningDistance = project.Settings.GuideSnap.AutoSnapWarningDistanceMeters;
        if (!confirmFarNodes && plan.GuideNodes.Any(n => n.DistanceToTrack > warningDistance))
            throw new InvalidOperationException($"有攻略节点距轨迹超过 {warningDistance:F0} 米，请确认后继续导入。");
        if (plan.Tracks.Any(t => project.Tracks.Any(existing => existing.Id == t.Id)))
            throw new InvalidOperationException("导入轨迹 ID 与项目中已有轨迹冲突。");
        project.ExecuteAtomicUpdate(p =>
        {
            var oldTracks = p.Tracks.ToArray();
            var oldWaypoints = p.Waypoints.ToArray();
            var oldNodes = p.GuideNodes.ToArray();
            var oldSegments = p.GuideSegments.ToArray();
            var oldRetired = p.RetiredGuideSegments.ToArray();
            var oldActive = p.ActiveTrackId;
            var oldMain = p.MainGuideTrackId;
            var oldBlobs = p.MediaBlobs.ToArray();
            var oldAttachments = p.MediaAttachments.ToArray();
            var oldExternal = p.ExternalMediaReferences.ToArray();
            try
            {
                foreach (var track in plan.Tracks) p.AddTrack(track);
                foreach (var waypoint in plan.Waypoints) p.Waypoints.Add(waypoint);
                foreach (var node in plan.GuideNodes) p.GuideNodes.Add(node);
                foreach (var blob in plan.MediaBlobs) p.MediaBlobs.Add(blob);
                foreach (var attachment in plan.MediaAttachments) p.MediaAttachments.Add(attachment);
                foreach (var reference in plan.ExternalMediaReferences) p.ExternalMediaReferences.Add(reference);
                p.MainGuideTrackId = plan.GuideTrackId;
                var segments = _guideService.RebuildSegments(p);
                GuideStructureService.ReplaceSegments(p, segments);
            }
            catch
            {
                Restore(p.Tracks, oldTracks);
                Restore(p.Waypoints, oldWaypoints);
                Restore(p.GuideNodes, oldNodes);
                Restore(p.GuideSegments, oldSegments);
                Restore(p.RetiredGuideSegments, oldRetired);
                Restore(p.MediaBlobs, oldBlobs);
                Restore(p.MediaAttachments, oldAttachments);
                Restore(p.ExternalMediaReferences, oldExternal);
                p.ActiveTrackId = oldActive;
                p.MainGuideTrackId = oldMain;
                throw;
            }
        });
    }

    private static void Restore<T>(System.Collections.ObjectModel.ObservableCollection<T> target, IReadOnlyList<T> original)
    {
        target.Clear();
        foreach (var item in original) target.Add(item);
    }

    private static Track? FindGuideTrack(ProjectImportSession session, HikingProject project)
    {
        var id = session.SelectedGuideTrackId;
        return id is null ? null : session.Parsed.Tracks.FirstOrDefault(t => t.Id == id && session.SelectedTrackIds.Contains(t.Id))
            ?? project.Tracks.FirstOrDefault(t => t.Id == id);
    }
}

public sealed record ProjectImportPlan(IReadOnlyList<Track> Tracks, Guid? GuideTrackId,
    IReadOnlyList<Waypoint> Waypoints, IReadOnlyList<GuideNode> GuideNodes,
    IReadOnlyList<MediaBlob>? NewMediaBlobs = null,
    IReadOnlyList<MediaAttachment>? NewMediaAttachments = null,
    IReadOnlyList<ExternalMediaReference>? NewExternalMediaReferences = null)
{
    public IReadOnlyList<MediaBlob> MediaBlobs => NewMediaBlobs ?? [];
    public IReadOnlyList<MediaAttachment> MediaAttachments => NewMediaAttachments ?? [];
    public IReadOnlyList<ExternalMediaReference> ExternalMediaReferences => NewExternalMediaReferences ?? [];
}
