using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;

public sealed class TrackEditSessionStateTests
{
    [Fact]
    public void SwitchTarget_KeepsProjectMode_AndRejectsPreviousDrag()
    {
        var (project, a, b) = Fixture();
        var state = new TrackEditSessionState();
        state.Begin(project, a);
        var previous = state.SessionId;
        Assert.Same(b, state.ResolveSelectionTarget(project, project.Id, previous, b.Id, b.GeometryRevision));
        Assert.True(state.SelectTarget(project, b));
        Assert.True(state.IsActive);
        Assert.Same(project, state.Project);
        Assert.NotEqual(previous, state.SessionId);
        Assert.False(state.IsCurrent(project, project.Id, previous, a.Id, a.GeometryRevision));
        Assert.False(state.IsCurrent(project, project.Id, previous, b.Id, b.GeometryRevision));
        Assert.True(state.IsCurrent(project, project.Id, state.SessionId, b.Id, b.GeometryRevision));
        Assert.False(project.IsDirty);
    }

    [Fact]
    public void SameTargetSelection_DoesNotInvalidateCurrentHandles()
    {
        var (project, a, _) = Fixture();
        var state = new TrackEditSessionState();
        state.Begin(project, a);
        var previous = state.SessionId;
        Assert.True(state.SelectTarget(project, a));
        Assert.Equal(previous, state.SessionId);
    }

    [Fact]
    public void RenewAfterUndo_RejectsOldMessageEvenWhenGeometryRevisionMatchesAgain()
    {
        var (project, a, _) = Fixture();
        var state = new TrackEditSessionState();
        state.Begin(project, a);
        var previous = state.SessionId;
        Assert.True(state.SelectTarget(project, a, renewSession: true));
        Assert.False(state.IsCurrent(project, project.Id, previous, a.Id, a.GeometryRevision));
        Assert.True(state.IsCurrent(project, project.Id, state.SessionId, a.Id, a.GeometryRevision));
    }

    [Fact]
    public void ForeignProjectOrTrack_CannotBecomeEditTarget()
    {
        var (project, a, _) = Fixture();
        var (foreign, foreignTrack, _) = Fixture();
        var state = new TrackEditSessionState();
        state.Begin(project, a);
        Assert.False(state.SelectTarget(foreign, foreignTrack));
        Assert.False(state.SelectTarget(project, foreignTrack));
        Assert.Null(state.ResolveSelectionTarget(foreign, foreign.Id, state.SessionId, foreignTrack.Id, 0));
        Assert.Null(state.ResolveSelectionTarget(project, project.Id, state.SessionId, foreignTrack.Id, 0));
        Assert.Throws<ArgumentException>(() => state.Begin(project, foreignTrack));
        Assert.Same(a, state.Track);
    }

    [Fact]
    public void RemovedTarget_RejectsPendingCommit_AndCanSelectRemainingTrack()
    {
        var (project, a, b) = Fixture();
        var state = new TrackEditSessionState();
        state.Begin(project, a);
        var previous = state.SessionId;
        project.Tracks.Remove(a);
        Assert.False(state.IsCurrent(project, project.Id, previous, a.Id, 0));
        Assert.Null(state.ResolveSelectionTarget(project, project.Id, previous, a.Id, 0));
        Assert.True(state.SelectTarget(project, b));
        Assert.Same(b, state.Track);
    }

    [Fact]
    public void OldGeometryOrOldSession_CannotSwitchTarget()
    {
        var (project, a, b) = Fixture();
        var state = new TrackEditSessionState();
        state.Begin(project, a);
        Assert.Null(state.ResolveSelectionTarget(project, project.Id, Guid.NewGuid(), b.Id, 0));
        Assert.Null(state.ResolveSelectionTarget(project, project.Id, state.SessionId, b.Id, b.GeometryRevision + 1));
        Assert.Null(state.ResolveSelectionTarget(project, Guid.NewGuid(), state.SessionId, b.Id, 0));
    }

    [Fact]
    public void Exit_RejectsAllPendingEvents_AndLeavesDataUnchanged()
    {
        var (project, a, _) = Fixture();
        var state = new TrackEditSessionState();
        state.Begin(project, a);
        var previous = state.SessionId;
        state.End();
        Assert.False(state.IsActive);
        Assert.Null(state.Project);
        Assert.Null(state.Track);
        Assert.Null(state.SessionId);
        Assert.False(state.IsCurrent(project, project.Id, previous, a.Id, 0));
        Assert.Null(state.ResolveSelectionTarget(project, project.Id, previous, a.Id, 0));
        Assert.Equal(2, project.Tracks.Count);
        Assert.False(project.IsDirty);
    }

    private static (HikingProject Project, Track A, Track B) Fixture()
    {
        var project = new HikingProject();
        var a = new Track { Name = "轨迹甲" };
        var b = new Track { Name = "轨迹乙" };
        project.Tracks.Add(a);
        project.Tracks.Add(b);
        project.MarkSaved();
        return (project, a, b);
    }
}
