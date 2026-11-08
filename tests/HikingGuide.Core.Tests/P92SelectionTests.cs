using HikingGuide.Core.Favorites;
namespace HikingGuide.Core.Tests;
public class P92SelectionTests
{
    [Fact] public void OneGlobalSelectionCanSwitchClearAndReconcileWithoutChangingRecords()
    {
        var service = new FavoriteSelectionService(); var a = new FavoritePoint { Name = "甲" }; var b = new FavoritePoint { Name = "乙" }; var changed = 0;
        service.SelectionChanged += (_, _) => changed++; service.SelectFavorite(a); Assert.Equal(a.Id, service.SelectedFavoritePointId);
        service.SelectFavorite(b); Assert.Equal(b, service.SelectedFavoritePoint); service.SelectFavorite(b); Assert.Equal(2, changed);
        service.Reconcile([a, b with { Name = "乙新" }]); Assert.Equal("乙新", service.SelectedFavoritePoint?.Name);
        service.Reconcile([a]); Assert.Null(service.SelectedFavoritePointId); Assert.Equal(4, changed); service.ClearSelection(); Assert.Equal(4, changed);
        Assert.Equal("甲", a.Name); Assert.Equal("乙", b.Name);
    }
    [Fact] public void MapStyleHasDifferentGoldAndPurpleAndNonzeroGlow()
    { var style = new FavoriteMapStyleProfile(); Assert.NotEqual(style.Color, style.SelectedColor); Assert.Contains(style.SelectedColor, style.SelectedSvgMarkup); Assert.True(style.GlowRadius > 0 && style.GlowOpacity > 0); }
}
