using HikingGuide.Infrastructure.Routing;
using Xunit;

namespace HikingGuide.Infrastructure.Tests;

public sealed class BRouterPathProviderTests
{
    [Fact]
    public void AllRoutingPaths_AreInsideApplicationBRouterDirectory()
    {
        var app = Path.Combine(Path.GetTempPath(), $"HikingGuide-P71-path-{Guid.NewGuid():N}");
        var paths = new BRouterPathProvider(app);
        Assert.Equal(Path.Combine(app, "BRouter"), paths.BRouterRoot);
        Assert.Equal(Path.Combine(app, "BRouter", "runtime"), paths.RuntimeDirectory);
        Assert.Equal(Path.Combine(app, "BRouter", "RoutingData", "current"),
            paths.CurrentRoutingDataDirectory);
        Assert.Equal(Path.Combine(app, "BRouter", "RoutingData", "staging"),
            paths.StagingDirectory);
        Assert.Equal(Path.Combine(app, "BRouter", "manifests"), paths.ManifestDirectory);
    }
}
