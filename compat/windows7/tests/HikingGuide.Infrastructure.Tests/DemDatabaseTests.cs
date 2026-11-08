using HikingGuide.Core.Geometry;
using HikingGuide.Infrastructure.Elevation;
using HikingGuide.Infrastructure.Projects;
using OSGeo.GDAL;
using OSGeo.OSR;

namespace HikingGuide.Infrastructure.Tests;

public sealed class DemDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine("F:\\Codex\\Temp", "HikingGuide-P8-Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ThreeMounts_PersistStableIds_AndNeverChangeSources()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        var raster = Path.Combine(source, "N23E113.tif");
        CreateRaster(raster, [10, 20, 30, 40]);
        var original = File.ReadAllBytes(raster);
        var appData = new ApplicationDataPaths(Path.Combine(_root, "app"));
        var catalog = new DemDatabaseCatalog(appData);
        var first = await catalog.MountAsync("甲", source, unitMetersConfirmed: true);
        var second = await catalog.MountAsync("乙", source, unitMetersConfirmed: true);
        var third = await catalog.MountAsync("丙", source, unitMetersConfirmed: true);
        Assert.Equal(3, catalog.Databases.Count);
        Assert.Equal(3, catalog.Databases.Select(item => item.Id).Distinct().Count());
        using var firstProvider = new DemElevationProvider(catalog);
        Assert.Equal(15, firstProvider.SampleDetailed(first.Database.Id,
            new GeoCoordinate(23.75, 113.5)).ElevationMeters);
        await catalog.RenameAsync(first.Database.Id, "甲-改名");
        var reloaded = new DemDatabaseCatalog(appData);
        await reloaded.LoadAsync();
        Assert.Equal(3, reloaded.Databases.Count);
        Assert.Equal("甲-改名", reloaded.Get(first.Database.Id).Name);
        Assert.Single(reloaded.FindCovering(second.Database.Id, new GeoCoordinate(23.75, 113.5)));
        await reloaded.RemoveMountAsync(third.Database.Id);
        Assert.Equal(2, reloaded.Databases.Count);
        Assert.Equal(original, File.ReadAllBytes(raster));
    }

    [Fact]
    public async Task Bilinear_NoData_AndTileEdges_AreNotZeroFilled()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        CreateRaster(Path.Combine(source, "N23E113.tif"), [10, 20, 30, 40]);
        var catalog = new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(_root, "app")));
        var mounted = await catalog.MountAsync("测试", source, unitMetersConfirmed: true);
        using var provider = new DemElevationProvider(catalog);
        Assert.Equal(25, provider.SampleDetailed(mounted.Database.Id,
            new GeoCoordinate(23.5, 113.5)).ElevationMeters);
        Assert.Equal(10, provider.SampleDetailed(mounted.Database.Id,
            new GeoCoordinate(24, 113)).ElevationMeters);
        Assert.Equal(DemSampleStatus.NoCoverage, provider.SampleDetailed(mounted.Database.Id,
            new GeoCoordinate(25, 115)).Status);
    }

    [Fact]
    public async Task NoDataPixel_IsNeverUsedForBilinearInterpolation()
    {
        var source = Path.Combine(_root, "nodata");
        Directory.CreateDirectory(source);
        CreateRaster(Path.Combine(source, "nodata.tif"), [10, 20, 30, -9999], -9999);
        var catalog = new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(_root, "app")));
        var mounted = await catalog.MountAsync("NoData", source, unitMetersConfirmed: true);
        using var provider = new DemElevationProvider(catalog);
        var sample = provider.SampleDetailed(mounted.Database.Id, new GeoCoordinate(23.5, 113.5));
        Assert.Equal(DemSampleStatus.NoData, sample.Status);
        Assert.Null(sample.ElevationMeters);
        Assert.Equal(10, provider.SampleDetailed(mounted.Database.Id,
            new GeoCoordinate(24, 113)).ElevationMeters);
    }

    [Fact]
    public async Task ProjectedUtmTif_IsTransformedFromWgs84BeforeSampling()
    {
        DemGdalRuntime.EnsureReady();
        using var wgs84 = new SpatialReference("");
        using var utm = new SpatialReference("");
        wgs84.SetFromUserInput("EPSG:4326");
        utm.SetFromUserInput("EPSG:32649");
        wgs84.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        utm.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        using var transform = new CoordinateTransformation(wgs84, utm);
        var xy = new double[3];
        transform.TransformPoint(xy, 113.5, 23.5, 0);
        var source = Path.Combine(_root, "utm");
        Directory.CreateDirectory(source);
        CreateRaster(Path.Combine(source, "utm.tif"), [10, 20, 30, 40], null,
            [xy[0] - 50, 50, 0, xy[1] + 50, 0, -50], "EPSG:32649");
        var catalog = new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(_root, "app")));
        var mounted = await catalog.MountAsync("UTM", source, unitMetersConfirmed: true);
        using var provider = new DemElevationProvider(catalog);
        Assert.Equal(25, provider.SampleDetailed(mounted.Database.Id,
            new GeoCoordinate(23.5, 113.5)).ElevationMeters!.Value, 4);
    }

    [Fact]
    public async Task DisconnectionAndRelocation_PreserveDatabaseIdAndSourceBytes()
    {
        var source = Path.Combine(_root, "first");
        Directory.CreateDirectory(source);
        var raster = Path.Combine(source, "sample.tif");
        CreateRaster(raster, [10, 20, 30, 40]);
        var bytes = File.ReadAllBytes(raster);
        var paths = new ApplicationDataPaths(Path.Combine(_root, "app"));
        var catalog = new DemDatabaseCatalog(paths);
        var mounted = await catalog.MountAsync("外置盘", source, unitMetersConfirmed: true);
        var destination = Path.Combine(_root, "moved");
        Directory.Move(source, destination);
        using var provider = new DemElevationProvider(catalog);
        Assert.Equal(DemSampleStatus.DatabaseDisconnected, provider.SampleDetailed(
            mounted.Database.Id, new GeoCoordinate(23.5, 113.5)).Status);
        var reloaded = new DemDatabaseCatalog(paths);
        await reloaded.LoadAsync();
        Assert.Equal(DemDatabaseStatus.Disconnected, reloaded.Get(mounted.Database.Id).Status);
        var relocated = await reloaded.RelocateAsync(mounted.Database.Id, destination);
        Assert.Equal(mounted.Database.Id, relocated.Database.Id);
        Assert.Equal(mounted.Database.Fingerprint, relocated.Database.Fingerprint);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(destination, "sample.tif")));
    }

    [Fact]
    public async Task RecursiveMixedScan_Recognizes1201Hgt_AndSkipsBrokenRaster()
    {
        var source = Path.Combine(_root, "mixed");
        var nested = Path.Combine(source, "nested");
        Directory.CreateDirectory(nested);
        CreateRaster(Path.Combine(source, "small.tif"), [10, 20, 30, 40]);
        var hgt = new byte[1201 * 1201 * 2];
        for (var index = 0; index < hgt.Length; index += 2) hgt[index + 1] = 100;
        File.WriteAllBytes(Path.Combine(nested, "N23E113.hgt"), hgt);
        File.WriteAllBytes(Path.Combine(nested, "broken.tif"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(nested, "notes.txt"), "ignore");
        var catalog = new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(_root, "app")));
        var scanned = await catalog.MountAsync("混合", source, unitMetersConfirmed: true);
        Assert.Equal(2, scanned.Database.ValidFileCount);
        Assert.Equal(1, scanned.Database.InvalidFileCount);
        Assert.Contains(scanned.Entries, item => item.Format == DemRasterFormat.Hgt &&
            item.PixelWidth == 1201);
        using var provider = new DemElevationProvider(catalog);
        Assert.Equal(100, provider.SampleDetailed(scanned.Database.Id,
            new GeoCoordinate(23.5, 113.5)).ElevationMeters!.Value, 6);
    }

    [Fact]
    public async Task FeetUnit_IsConvertedToMeters_WithoutChangingRaster()
    {
        var source = Path.Combine(_root, "feet");
        Directory.CreateDirectory(source);
        var path = Path.Combine(source, "feet.tif");
        CreateRaster(path, [100, 100, 100, 100], unit: "ft");
        var original = File.ReadAllBytes(path);
        var catalog = new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(_root, "app")));
        var mounted = await catalog.MountAsync("英尺", source);
        using var provider = new DemElevationProvider(catalog);
        Assert.Equal(30.48, provider.SampleDetailed(mounted.Database.Id,
            new GeoCoordinate(23.5, 113.5)).ElevationMeters!.Value, 4);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task UnknownUnit_RequiresExplicitMetersConfirmation()
    {
        var source = Path.Combine(_root, "unknown-unit");
        Directory.CreateDirectory(source);
        CreateRaster(Path.Combine(source, "unknown.tif"), [10, 20, 30, 40], unit: "");
        var catalog = new DemDatabaseCatalog(new ApplicationDataPaths(Path.Combine(_root, "app")));
        var mounted = await catalog.MountAsync("未知单位", source);
        using var provider = new DemElevationProvider(catalog);
        var sample = provider.SampleDetailed(mounted.Database.Id, new GeoCoordinate(23.5, 113.5));
        Assert.Equal(DemSampleStatus.InvalidUnit, sample.Status);
        Assert.Null(sample.ElevationMeters);
    }

    private static void CreateRaster(string path, double[] values,
        double? noData = null, double[]? geoTransform = null, string crs = "EPSG:4326",
        string unit = "m")
    {
        DemGdalRuntime.EnsureReady();
        using var dataset = Gdal.GetDriverByName("GTiff").Create(path, 2, 2, 1,
            DataType.GDT_Float64, null);
        dataset.SetGeoTransform(geoTransform ?? [113, 0.5, 0, 24, 0, -0.5]);
        using var wgs84 = new SpatialReference("");
        wgs84.SetFromUserInput(crs);
        wgs84.ExportToWkt(out var wkt, null);
        dataset.SetProjection(wkt);
        using var band = dataset.GetRasterBand(1);
        band.SetUnitType(unit);
        if (noData.HasValue) band.SetNoDataValue(noData.Value);
        band.WriteRaster(0, 0, 2, 2, values, 2, 2, 0, 0);
        band.FlushCache();
        dataset.FlushCache();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
