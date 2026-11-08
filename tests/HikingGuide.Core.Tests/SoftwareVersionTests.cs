using System.Reflection;
using HikingGuide.Core.Software;

namespace HikingGuide.Core.Tests;

public sealed class SoftwareVersionTests
{
    [Fact]
    public void DisplayAndWindowsVersionsAreConsistent()
    {
        var assembly=typeof(SoftwareIdentity).Assembly;
        Assert.Equal("T1.9.2",SoftwareIdentity.DisplayVersion);
        Assert.Equal("易行徒步规划制作器 T1.9.2",SoftwareIdentity.DisplayTitle);
        Assert.Equal(new Version(1,9,2,0),assembly.GetName().Version);
        Assert.Equal("1.9.2.0",assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
    }
}
