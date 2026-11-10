using HikingGuide.Core.Favorites;
namespace HikingGuide.Core.Tests;
public class FavoriteExportSelectionTests
{
    [Fact]public void NamesFollowOnePointOrCollectionAndAreWindowsSafe(){var p=new FavoritePoint{Name="山顶：营地/北"};var now=DateTimeOffset.UtcNow;var c=new FavoriteCollection(Guid.NewGuid(),"山脊收藏夹",0,now,now);Assert.Equal("山顶：营地_北",new FavoriteExportSelection(FavoriteExportScope.Points,[],[p.Id]).DefaultFileName([c],[p]));Assert.Equal(c.Name,new FavoriteExportSelection(FavoriteExportScope.Collections,[c.Id],[]).DefaultFileName([c],[p]));Assert.Equal("我的收藏点",new FavoriteExportSelection(FavoriteExportScope.All,[],[]).DefaultFileName([c],[p]));Assert.Equal("_CON",new FavoriteExportSelection(FavoriteExportScope.Points,[],[p.Id]).DefaultFileName([],[p with{Name="CON"}]));}
    [Fact]public void EmptyExplicitSelectionsAreRejected(){Assert.Throws<ArgumentException>(()=>new FavoriteExportSelection(FavoriteExportScope.Points,[],[]).Validate());Assert.Throws<ArgumentException>(()=>new FavoriteExportSelection(FavoriteExportScope.Collections,[],[]).Validate());new FavoriteExportSelection(FavoriteExportScope.All,[],[]).Validate();}
}
