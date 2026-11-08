namespace HikingGuide.Infrastructure.Routing;

public interface IBRouterPathProvider
{
    string BRouterRoot { get; }
    string RuntimeDirectory { get; }
    string ProfileDirectory { get; }
    string RoutingDataDirectory { get; }
    string CurrentRoutingDataDirectory { get; }
    string StagingDirectory { get; }
    string ManifestDirectory { get; }
    string LogDirectory { get; }
    void EnsureWritable();
}

/// <summary>唯一的软件内 BRouter 路径来源；不在失败时回退到缓存或临时目录。</summary>
public sealed class BRouterPathProvider : IBRouterPathProvider
{
    public BRouterPathProvider(string? applicationDirectory = null)
    {
        var applicationRoot = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        var dataRoot = HikingGuide.Infrastructure.Projects.ApplicationDataPaths.FunctionRootDirectory(applicationRoot,
            applicationDirectory is null ? Environment.GetEnvironmentVariable("HIKINGGUIDE_FUNCTION_DATA_ROOT") : null);
        BRouterRoot = Path.Combine(dataRoot, "BRouter");
        RuntimeDirectory = Path.Combine(applicationRoot, "BRouter", "runtime");
    }

    public string BRouterRoot { get; }
    public string RuntimeDirectory { get; }
    public string ProfileDirectory => Path.Combine(RuntimeDirectory, "profiles2");
    public string RoutingDataDirectory => Path.Combine(BRouterRoot, "RoutingData");
    public string CurrentRoutingDataDirectory => Path.Combine(RoutingDataDirectory, "current");
    public string StagingDirectory => Path.Combine(RoutingDataDirectory, "staging");
    public string ManifestDirectory => Path.Combine(BRouterRoot, "manifests");
    public string LogDirectory => Path.Combine(BRouterRoot, "logs");

    public void EnsureWritable()
    {
        try
        {
            Directory.CreateDirectory(RoutingDataDirectory);
            var probe = Path.Combine(RoutingDataDirectory, $".write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new IOException("BRouter功能目录不可写，无法保存路由数据。", exception);
        }
    }
}
