using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HikingGuide.Core.Elevation;
using HikingGuide.Infrastructure.Elevation;
using HikingGuide.Infrastructure.Projects;

// 只加载已有索引和只读DEM，不保存配置、不写地图缓存。
if (args.Length != 1) throw new ArgumentException("请传入包含dem-databases.json的配置目录。");
var catalog = new DemDatabaseCatalog(new ApplicationDataPaths(Path.GetFullPath(args[0])));
await catalog.LoadAsync();
var results = new List<object>();
foreach (var database in catalog.Databases)
{
    using var provider = new DemElevationProvider(catalog, maximumOpenHandles: 4, rasterWindowCacheCapacity: 32);
    var sampler = new TerrainTileSampler(catalog, provider);
    var choice = sampler.Describe(database.Id);
    if (choice is null) continue;
    var z = choice.MaxNativeZoom;
    var x = (int)((113.25442 + 180) / 360 * (1 << z));
    var y = (int)((1 - Math.Asinh(Math.Tan(24.483696 * Math.PI / 180)) / Math.PI) / 2 * (1 << z));
    for (var pass = 0; pass < 4; pass++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var pixels = sampler.Sample(database.Id, z, x, y);
        watch.Stop();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var result = new { Database = database.Name, Pass = pass, Zoom = z, X = x, Y = y,
            Milliseconds = watch.Elapsed.TotalMilliseconds, AllocatedBytes = bytes,
            Sha256 = Convert.ToHexString(SHA256.HashData(pixels)), provider.CacheStatus.OpenHandles };
        results.Add(result);
        Console.Error.WriteLine($"{database.Name} 第{pass}轮：{watch.Elapsed.TotalSeconds:0.000}秒，分配{bytes:N0}字节");
    }
}
Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
