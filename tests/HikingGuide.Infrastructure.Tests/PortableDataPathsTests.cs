using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.Favorites;

namespace HikingGuide.Infrastructure.Tests;

public sealed class PortableDataPathsTests
{
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"EasyHiking-InstalledPaths-"+Guid.NewGuid().ToString("N"));
        public TemporaryDirectory()=>Directory.CreateDirectory(Path);
        public void Dispose()=>Directory.Delete(Path,true);
    }
    [Fact]
    public void InstalledModeSeparatesUserDataFromReadOnlyProgramAndJava()
    {
        using var directory=new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path,ApplicationDataPaths.InstalledModeMarker),"[Installation]\nDataMode=CurrentUser");
        var userRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EasyHiking");
        Assert.True(ApplicationDataPaths.IsInstalledApplication(directory.Path));
        Assert.Equal(userRoot,ApplicationDataPaths.DefaultFunctionRootDirectory(directory.Path));
        Assert.Equal(Path.Combine(userRoot,"Data"),ApplicationDataPaths.DefaultRootDirectory(directory.Path));
        Assert.Equal(userRoot,ApplicationDataPaths.FunctionRootDirectory(directory.Path,null));
        Assert.Equal(Path.Combine(userRoot,"BRouter"),new HikingGuide.Infrastructure.Routing.BRouterPathProvider(directory.Path).BRouterRoot);
        Assert.Equal(Path.Combine(directory.Path,"BRouter","runtime"),new HikingGuide.Infrastructure.Routing.BRouterPathProvider(directory.Path).RuntimeDirectory);
        Assert.Equal(Path.GetFullPath("F:\\Codex\\Temp\\isolated"),ApplicationDataPaths.FunctionRootDirectory(directory.Path,"F:\\Codex\\Temp\\isolated"));
    }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")]
    public void MissingOrEmptyFunctionOverrideUsesRuntimeDirectory(string? configured)
    {
        var app="F:\\Hiking Guide\\易行徒步规划制作器";
        Assert.Equal(Path.GetFullPath(app),ApplicationDataPaths.FunctionRootDirectory(app,configured));
    }
    [Theory]
    [InlineData("F:\\Hiking Guide\\易行徒步规划制作器")]
    [InlineData("D:\\用户软件\\易行徒步规划制作器")]
    public void DefaultsFollowApplicationDirectoryWithoutHardcodedDrive(string applicationDirectory)
    {
        Assert.Equal(Path.Combine(Path.GetFullPath(applicationDirectory), "Data"), ApplicationDataPaths.DefaultRootDirectory(applicationDirectory));
        Assert.Equal(Path.Combine(Path.GetFullPath(applicationDirectory), "Favorites"), new FavoritesPathProvider(applicationDirectory).FavoritesRoot);
    }
    [Fact]
    public void ExplicitIsolatedDataRootStillOverridesDefaults()
    {
        var root = Path.Combine("F:\\Codex\\Temp", "portable-data-test");
        var paths = new ApplicationDataPaths(root);
        Assert.Equal(Path.GetFullPath(root), paths.RootDirectory);
        Assert.Equal(Path.Combine(root,"recent-projects.json"), paths.RecentProjectsFile);
        Assert.Equal(Path.Combine(root,"AutoSave"), paths.AutoSaveDirectory);
    }
}
