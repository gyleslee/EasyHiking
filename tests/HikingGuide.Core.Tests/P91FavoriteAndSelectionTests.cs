using HikingGuide.Core.Favorites;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Core.Tests;

public sealed class P91FavoriteAndSelectionTests
{
    [Theory]
    [InlineData("Leave")][InlineData("Scroll")][InlineData("Search")][InlineData("Collection")]
    [InlineData("Hidden")][InlineData("Deactivated")][InlineData("Deleted")][InlineData("Edited")]
    public void ClosingHoverCancelsPendingAndRejectsOldThumbnail(string reason)
    {
        Assert.False(string.IsNullOrWhiteSpace(reason));
        using var session = new FavoriteHoverSession(); var id = Guid.NewGuid(); var target = new object(); var request = session.Enter(id, target);
        Assert.True(session.TryOpen(id, target, request.Generation)); session.Close();
        Assert.True(request.Token.IsCancellationRequested); Assert.False(session.IsOpen); Assert.Null(session.CurrentFavoriteId);
        Assert.False(session.TryOpen(id, target, request.Generation)); Assert.True(session.CloseRequestId > session.OpenRequestId);
    }
    [Fact]
    public void TwentyRapidHoverCyclesNeverAllowOldRequestsToReopen()
    {
        using var session = new FavoriteHoverSession();
        for (var i = 0; i < 20; i++)
        {
            var a = Guid.NewGuid(); var b = Guid.NewGuid(); var aTarget = new object(); var bTarget = new object();
            var old = session.Enter(a, aTarget); session.Close(); var current = session.Enter(b, bTarget);
            Assert.False(session.TryOpen(a, aTarget, old.Generation)); Assert.True(session.TryOpen(b, bTarget, current.Generation));
            Assert.Equal(b, session.CurrentFavoriteId); session.Close(); Assert.False(session.IsOpen);
        }
    }
    [Fact]
    public void NameOrderingIsChineseCaseInsensitiveAndHasStableIdTieBreak()
    {
        var points = new[] { "船底顶", "b point", "白云山", "阿尔山", "A Point" }.Select(n => new FavoritePoint { Name = n }).ToArray();
        var result = FavoriteNameOrdering.Sort(points).Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "阿尔山", "白云山", "船底顶", "A Point", "b point" }, result);
        Assert.Equal(0, FavoriteNameOrdering.Comparer.Compare("A Point", "a point"));
        Assert.Equal(FavoriteNameOrdering.Sort(points).Select(p => p.Id), FavoriteNameOrdering.Sort(points.Reverse()).Select(p => p.Id));
        var same = new[] { new FavoritePoint { Name = "相同", Id = Guid.Parse("00000000-0000-0000-0000-000000000002") }, new FavoritePoint { Name = "相同", Id = Guid.Parse("00000000-0000-0000-0000-000000000001") } };
        Assert.Equal(same[1].Id, FavoriteNameOrdering.Sort(same)[0].Id);
    }
    [Theory]
    [InlineData("Track")][InlineData("Split")][InlineData("Guide")][InlineData("Waypoint")][InlineData("GuideSegment")]
    public void AssociatedObjectsResolveActualParentNotFirstTrack(string kind)
    {
        var project = new HikingProject(); var first = new Track(); var second = new Track(); project.AddTrack(first); project.AddTrack(second);
        object model = kind switch { "Track" => second, "Split" => new SplitSection(Guid.NewGuid(), second.Id, "分段", [], 0), "Guide" => new GuideNode { TrackId = second.Id },
            "Waypoint" => new Waypoint { AssociatedTrackId = second.Id }, _ => new GuideSegment { TrackId = second.Id } };
        Assert.Equal(second.Id, PrimaryTrackSelection.Resolve(project, model));
    }
    [Fact]
    public void UnassociatedOrForeignObjectDoesNotFallBackToFirstTrack()
    {
        var project = new HikingProject(); project.AddTrack(new Track());
        Assert.Null(PrimaryTrackSelection.Resolve(project, new Waypoint())); Assert.Null(PrimaryTrackSelection.Resolve(project, new Track())); Assert.Null(PrimaryTrackSelection.Resolve(project, null));
    }
    [Fact]
    public void TrackRenameNotifiesWithoutChangingSerializedNameContract()
    {
        var track = new Track { Name = "A" }; var calls = 0; track.PropertyChanged += (_, e) => { Assert.Equal("Name", e.PropertyName); calls++; };
        track.Name = "A-new"; track.Name = "A-new"; Assert.Equal(1, calls);
    }
}
