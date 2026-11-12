using HikingGuide.Application.Logging;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Maps;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.Infrastructure.Tests;

public sealed class GlobalSettingsServiceTests
{
    [Fact]public async Task UnifiedMapDemUsesTerrainChoiceAndMigratesOnlyOnceWithoutTouchingOtherSettings()
    {
        using var directory=new TemporaryDirectory();
        GlobalSettingsService Service()=>new(new ApplicationDataPaths(directory.Path),new TestLoggingService(),new MapSourceTemplateService());
        var service=Service();await service.LoadAsync();var terrain=Guid.NewGuid();var oldContour=Guid.NewGuid();
        service.Current.Terrain3D=new(){TerrainDemDatabaseId=terrain,Enabled=false,Exaggeration=1.2};
        service.Current.OfflineTerrain=new(){DemDatabaseId=oldContour,ContourOpacity=.37,HillshadeOpacity=.6};
        service.Current.FlatSlopeThresholdPercent=2.1;var sources=service.Current.MapSources.Select(s=>s.Id).ToArray();
        await service.SaveAsync();var reopened=Service();await reopened.LoadAsync();
        Assert.Equal(1,reopened.Current.UnifiedMapDemVersion);Assert.Equal(terrain,reopened.Current.OfflineTerrain.DemDatabaseId);
        Assert.Equal(.37,reopened.Current.OfflineTerrain.ContourOpacity);Assert.Equal(.6,reopened.Current.OfflineTerrain.HillshadeOpacity);
        Assert.Equal(1.2,reopened.Current.Terrain3D!.Exaggeration);Assert.False(reopened.Current.Terrain3D.Enabled);
        Assert.Equal(2.1,reopened.Current.FlatSlopeThresholdPercent);Assert.Equal(sources,reopened.Current.MapSources.Select(s=>s.Id));
        reopened.Current.Terrain3D=reopened.Current.Terrain3D with{TerrainDemDatabaseId=null};await reopened.SaveAsync();
        Assert.Null(reopened.Current.OfflineTerrain.DemDatabaseId);
    }
    [Fact]public async Task UnifiedMapDemCanAdoptLegacyContourChoiceWhenTerrainChoiceWasNeverSet()
    {
        using var directory=new TemporaryDirectory();var service=new GlobalSettingsService(new ApplicationDataPaths(directory.Path),new TestLoggingService(),new MapSourceTemplateService());
        await service.LoadAsync();var legacy=Guid.NewGuid();service.Current.OfflineTerrain=service.Current.OfflineTerrain with{DemDatabaseId=legacy};
        await service.SaveAsync();Assert.Equal(legacy,service.Current.Terrain3D!.TerrainDemDatabaseId);
    }
    [Fact]public async Task SavedRoadLevelsPersistAsDefaultsAndNewUsersReadPackageBaselineOnlyOnce()
    {
        using var directory=new TemporaryDirectory();var defaults=Path.Combine(directory.Path,"Defaults");Directory.CreateDirectory(defaults);
        var id=OsmRoadDefaults.CreateStyles()[0].Id;
        await File.WriteAllTextAsync(Path.Combine(defaults,"osm-road-default-levels.json"),System.Text.Json.JsonSerializer.Serialize(new Dictionary<string,OsmRoadZoomRange>{{id,new(15,20)}}));
        GlobalSettingsService Service()=>new(new ApplicationDataPaths(Path.Combine(directory.Path,"Data")),new TestLoggingService(),new MapSourceTemplateService(),defaults);
        var service=Service();await service.LoadAsync();Assert.Equal(15,service.Current.OsmRoads.Styles[0].MinZoom);Assert.Equal(20,service.Current.OsmRoads.Styles[0].MaxZoom);
        service.Current.OsmRoads.Styles[0].MinZoom=16;service.Current.OsmRoads.Styles[0].Color="#123456";await service.SaveAsync();
        var reopened=Service();await reopened.LoadAsync();Assert.Equal(16,reopened.Current.OsmRoads.Styles[0].MinZoom);reopened.Current.OsmRoads.RestoreDefaultStyles();Assert.Equal(16,reopened.Current.OsmRoads.Styles[0].MinZoom);Assert.NotEqual("#123456",reopened.Current.OsmRoads.Styles[0].Color);
    }
    [Fact]public async Task RoadDisplayMigrationIsSavedOnceAndPreservesCustomStyles()
    {
        using var directory=new TemporaryDirectory();var service=Create(directory.Path);await service.LoadAsync();
        service.Current.OsmRoads.DisplayRulesVersion=0;service.Current.OsmRoads.Styles[0].Color="#123456";service.Current.OsmRoads.Styles[0].MaxZoom=15;
        await service.SaveAsync();var reloaded=Create(directory.Path);await reloaded.LoadAsync();
        Assert.All(reloaded.Current.OsmRoads.Styles,s=>Assert.Equal(22,s.MaxZoom));Assert.NotEqual("#123456",reloaded.Current.OsmRoads.Styles[0].Color);
        reloaded.Current.OsmRoads.Styles[0].MaxZoom=20;await reloaded.SaveAsync();var again=Create(directory.Path);await again.LoadAsync();Assert.Equal(20,again.Current.OsmRoads.Styles[0].MaxZoom);
        again.Current.OsmRoads.RestoreDefaultStyles();Assert.Equal(20,again.Current.OsmRoads.Styles[0].MaxZoom);
    }
    [Fact]public async Task ReleaseDefaultsAndExistingLayoutAndTerrainChoicesStayIndependent()
    {
        using var directory=new TemporaryDirectory();var service=Create(directory.Path);await service.LoadAsync();
        Assert.Equal(MainWindowPanelLayout.Default,service.Current.PanelLayout);
        Assert.Equal(1.5,new Terrain3DSettings().Exaggeration);
        var customLayout=new MainWindowPanelLayout(.42,.2,.25);
        service.Current.PanelLayout=customLayout;service.Current.Terrain3D=new(){Exaggeration=1.2};
        await service.SaveAsync();var reloaded=Create(directory.Path);await reloaded.LoadAsync();
        Assert.Equal(customLayout,reloaded.Current.PanelLayout);Assert.Equal(1.2,reloaded.Current.Terrain3D!.Exaggeration);
    }
    [Fact]public async Task NewDefaultsEnableContoursAndThirtyPercentShadeButPreserveUserChoices()
    {
        using var directory=new TemporaryDirectory();var service=Create(directory.Path);await service.LoadAsync();
        Assert.True(service.Current.OfflineTerrain.ContoursVisible);Assert.True(service.Current.OfflineTerrain.HillshadeVisible);
        Assert.Equal(.3,service.Current.OfflineTerrain.HillshadeOpacity);Assert.False(service.Current.FirstRunSetupCompleted);
        service.Current.OfflineTerrain=service.Current.OfflineTerrain with{ContoursVisible=false,HillshadeVisible=false,HillshadeOpacity=.7};
        service.Current.FirstRunSetupCompleted=true;await service.SaveAsync();
        var reopened=Create(directory.Path);await reopened.LoadAsync();
        Assert.False(reopened.Current.OfflineTerrain.ContoursVisible);Assert.False(reopened.Current.OfflineTerrain.HillshadeVisible);
        Assert.Equal(.7,reopened.Current.OfflineTerrain.HillshadeOpacity);Assert.True(reopened.Current.FirstRunSetupCompleted);
    }
    [Fact]public async Task SourceCoordinateSystemsRoundTripAndLegacyDefaultsToWgs84()
    {
        using var directory=new TemporaryDirectory();var service=Create(directory.Path);await service.LoadAsync();
        Assert.All(service.Current.MapSources,s=>Assert.Equal(MapSourceCoordinateSystem.Wgs84,s.CoordinateSystem));
        var gcj=new MapSourceDefinition{Name="GCJ图源",UrlTemplate="http://webst{$s}.is.autonavi.com/appmaptile?x={x}&y={y}&z={z}",CoordinateSystem=MapSourceCoordinateSystem.Gcj02};
        var bd=new MapSourceDefinition{Name="BD图源",UrlTemplate="https://tiles.example/{z}/{x}/{y}",CoordinateSystem=MapSourceCoordinateSystem.Bd09};
        service.Current.MapSources.AddRange([gcj,bd]);await service.SaveAsync();var reopened=Create(directory.Path);await reopened.LoadAsync();
        Assert.Equal(MapSourceCoordinateSystem.Gcj02,reopened.Current.MapSources.Single(s=>s.Id==gcj.Id).CoordinateSystem);
        Assert.Equal(gcj.UrlTemplate,reopened.Current.MapSources.Single(s=>s.Id==gcj.Id).UrlTemplate);
        Assert.Equal(MapSourceCoordinateSystem.Bd09,reopened.Current.MapSources.Single(s=>s.Id==bd.Id).CoordinateSystem);
    }
    [Fact]
    public async Task PanelVisibilityAndContourOpacityPersistGlobally()
    {
        using var directory=new TemporaryDirectory();var service=Create(directory.Path);await service.LoadAsync();
        Assert.Equal(new PanelVisibilitySettings(),service.Current.PanelVisibility);
        Assert.Equal(1,service.Current.OfflineTerrain.ContourOpacity);
        service.Current.PanelVisibility=new(){Map=false,Analysis=false,Status=false};
        service.Current.OfflineTerrain=service.Current.OfflineTerrain with{ContourOpacity=.37};
        service.Current.PanelLayout=new(.43,.21,.24);await service.SaveAsync();
        var reopened=Create(directory.Path);await reopened.LoadAsync();
        Assert.Equal(service.Current.PanelVisibility,reopened.Current.PanelVisibility);
        Assert.Equal(.37,reopened.Current.OfflineTerrain.ContourOpacity);
        Assert.Equal(service.Current.PanelLayout,reopened.Current.PanelLayout);
    }
    [Theory]
    [InlineData(-.1)] [InlineData(1.1)] [InlineData(double.NaN)]
    public async Task InvalidContourOpacityDoesNotEraseOtherSettings(double opacity)
    {
        using var directory=new TemporaryDirectory();var service=Create(directory.Path);await service.LoadAsync();
        var id=Guid.NewGuid();service.Current.OfflineTerrain=new(){DemDatabaseId=id,ContourOpacity=opacity,ContourColor="#123456",ContourCacheMb=123};
        await service.SaveAsync();var reopened=Create(directory.Path);await reopened.LoadAsync();
        Assert.Equal(1,reopened.Current.OfflineTerrain.ContourOpacity);
        Assert.Equal(id,reopened.Current.OfflineTerrain.DemDatabaseId);
        Assert.Equal("#123456",reopened.Current.OfflineTerrain.ContourColor);
        Assert.Equal(123,reopened.Current.OfflineTerrain.ContourCacheMb);
    }
    [Fact]
    public async Task OldSettingsDefaultToVisiblePanelsAndOpaqueContours()
    {
        using var directory=new TemporaryDirectory();var service=Create(directory.Path);
        await File.WriteAllTextAsync(service.SettingsFilePath,"{\"offlineTerrain\":{\"contourColor\":\"#123456\"}}");
        await service.LoadAsync();
        Assert.Equal(new PanelVisibilitySettings(),service.Current.PanelVisibility);
        Assert.Equal(1,service.Current.OfflineTerrain.ContourOpacity);
        Assert.Equal("#123456",service.Current.OfflineTerrain.ContourColor);
    }
    [Fact]
    public async Task MajorContoursPersistAndInvalidIntervalDoesNotEraseOtherOfflineSettings()
    {
        using var directory=new TemporaryDirectory();var service=Create(directory.Path);await service.LoadAsync();var dem=Guid.NewGuid();
        service.Current.OfflineTerrain=new(){DemDatabaseId=dem,ContoursVisible=true,HillshadeVisible=true,MajorContoursEnabled=true,MajorContourIntervalMeters=100};
        await service.SaveAsync();var reopened=Create(directory.Path);await reopened.LoadAsync();
        Assert.Equal(100,reopened.Current.OfflineTerrain.MajorContourIntervalMeters);Assert.Equal(dem,reopened.Current.OfflineTerrain.DemDatabaseId);
        reopened.Current.OfflineTerrain=reopened.Current.OfflineTerrain with{MajorContourIntervalMeters=0};await reopened.SaveAsync();
        Assert.Equal(50,reopened.Current.OfflineTerrain.MajorContourIntervalMeters);Assert.Equal(dem,reopened.Current.OfflineTerrain.DemDatabaseId);
        Assert.True(reopened.Current.OfflineTerrain.ContoursVisible);Assert.True(reopened.Current.OfflineTerrain.HillshadeVisible);
    }
    [Fact]
    public async Task UnifiedTerrainSettingsAndGlobalCameraPreserve2DDisplayParameters()
    {
        using var directory=new TemporaryDirectory();var service=Create(directory.Path);await service.LoadAsync();
        var dem2D=Guid.NewGuid();var dem3D=Guid.NewGuid();
        service.Current.OfflineTerrain=new(){DemDatabaseId=dem2D,ContoursVisible=false,HillshadeVisible=true,ContourCacheMb=123,HillshadeCacheMb=456};
        service.Current.Terrain3D=new(){Enabled=true,TerrainDemDatabaseId=dem3D,Exaggeration=1.37,DefaultPitch=75,ShowContours=true,ShowHillshade=false,CacheLimitMb=5120};
        service.Current.TerrainCamera=new(24.42,113.32,15,90,55);var original=service.Current.OfflineTerrain;await service.SaveAsync();
        var reopened=Create(directory.Path);await reopened.LoadAsync();
        Assert.Equal(1.4,reopened.Current.Terrain3D!.Exaggeration);
        Assert.Equal(service.Current.Terrain3D,reopened.Current.Terrain3D);Assert.Equal(service.Current.TerrainCamera,reopened.Current.TerrainCamera);
        Assert.Equal(original with{DemDatabaseId=dem3D},reopened.Current.OfflineTerrain);Assert.Equal(2.1,reopened.Current.FlatSlopeThresholdPercent);
    }
    [Fact]
    public async Task TerrainMigrationKeepsOldVisibilityAndRejectsInvalidSettings()
    {
        using var directory=new TemporaryDirectory();var service=Create(directory.Path);await service.LoadAsync();
        service.Current.OfflineTerrain=new(){DemDatabaseId=Guid.NewGuid(),ContoursVisible=false,HillshadeVisible=false,ScreenAlignedLabels=true};
        service.Current.Terrain3D=null;await service.SaveAsync();
        Assert.False(service.Current.Terrain3D!.ShowContours);Assert.False(service.Current.Terrain3D.ShowHillshade);Assert.False(service.Current.OfflineTerrain.ScreenAlignedLabels);
        service.Current.Terrain3D=service.Current.Terrain3D with{Exaggeration=9};service.Current.TerrainCamera=new(0,0,double.NaN,0,55);
        await service.SaveAsync();Assert.Equal(1.5,service.Current.Terrain3D!.Exaggeration);Assert.Null(service.Current.TerrainCamera);
    }
    [Fact]
    public async Task GuideColumnWidthsRoundTripAcrossRestartAndPreserveExistingSettings()
    {
        using var directory = new TemporaryDirectory(); var service = Create(directory.Path); await service.LoadAsync();
        Assert.Empty(service.Current.GuideTableColumnWidths);
        service.Current.GuideTableColumnWidths = new() { ["Guide:GuideNodeName"] = 245, ["Guide:Plan:行进方案1"] = 180, ["Guide:Location:营地"] = 123, ["Daily:Start"] = 234 };
        service.Current.PanelLayout = new(.5, .2, .2); await service.SaveAsync();
        var reopened = Create(directory.Path); await reopened.LoadAsync();
        Assert.Equal(service.Current.GuideTableColumnWidths, reopened.Current.GuideTableColumnWidths);
        Assert.Equal(service.Current.PanelLayout, reopened.Current.PanelLayout);
        Assert.Equal(service.Current.DefaultMapSourceId, reopened.Current.DefaultMapSourceId);
    }
    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(5000)] [InlineData(double.NaN)]
    public async Task InvalidColumnWidthDoesNotDestroyValidWidths(double value)
    {
        using var directory = new TemporaryDirectory(); var service = Create(directory.Path); await service.LoadAsync();
        service.Current.GuideTableColumnWidths = new() { ["Guide:Elevation"] = value, ["Guide:GuideNodeName"] = 222 };
        await service.SaveAsync(); var reopened = Create(directory.Path); await reopened.LoadAsync();
        Assert.False(reopened.Current.GuideTableColumnWidths.ContainsKey("Guide:Elevation"));
        Assert.Equal(222, reopened.Current.GuideTableColumnWidths["Guide:GuideNodeName"]);
        Assert.NotEmpty(reopened.Current.MapSources);
    }

    [Fact]
    public async Task PanelLayoutSurvivesRestartWithoutChangingOtherSettings()
    {
        using var directory = new TemporaryDirectory();
        var service = Create(directory.Path); await service.LoadAsync();
        Assert.Equal(MainWindowPanelLayout.Default,service.Current.PanelLayout);
        service.Current.PanelLayout = new(.47, .19, .23);
        service.Current.ShowFavoritesOnMap = false;
        await service.SaveAsync();
        var reopened = Create(directory.Path); await reopened.LoadAsync();
        Assert.Equal(service.Current.PanelLayout, reopened.Current.PanelLayout);
        Assert.False(reopened.Current.ShowFavoritesOnMap);
        Assert.Equal(service.Current.DefaultMapSourceId, reopened.Current.DefaultMapSourceId);
    }

    [Theory]
    [InlineData(-1, .2, .2)]
    [InlineData(1, .2, .2)]
    [InlineData(.4, -.2, .2)]
    [InlineData(.4, .6, .5)]
    [InlineData(double.NaN, .2, .2)]
    public async Task InvalidPanelLayoutFallsBackWithoutLosingMapSettings(double bottom, double project, double properties)
    {
        using var directory = new TemporaryDirectory();
        var service = Create(directory.Path); await service.LoadAsync();
        service.Current.PanelLayout = new(bottom, project, properties);
        await service.SaveAsync();
        var reopened = Create(directory.Path); await reopened.LoadAsync();
        Assert.Null(reopened.Current.PanelLayout);
        Assert.NotEmpty(reopened.Current.MapSources);
    }

    [Fact]
    public async Task TerrainThresholdDefaultsToApprovedValueAndSurvivesRestart()
    {
        using var directory = new TemporaryDirectory();
        var service = Create(directory.Path); await service.LoadAsync();
        Assert.Equal(new HikingGuide.Core.Elevation.LocusTerrainDistanceProfile().FlatSlopeThresholdPercent,
            service.Current.FlatSlopeThresholdPercent);
        service.Current.FlatSlopeThresholdPercent = 3.5;
        await service.SaveAsync();
        var reopened = Create(directory.Path); await reopened.LoadAsync();
        Assert.Equal(3.5, reopened.Current.FlatSlopeThresholdPercent);
    }
    [Fact]
    public async Task SaveAndLoad_RoundTripsMapSourceHeaders()
    {
        using var directory = new TemporaryDirectory();
        var service = Create(directory.Path);
        await service.LoadAsync();
        var custom = new MapSourceDefinition
        {
            Name = "带鉴权图源",
            UrlTemplate = "https://tiles.example/{z}/{x}/{y}.png?token=secret",
            UserAgent = "HikingGuide.Tests",
            Referer = "https://example.test/",
            Headers = new() { ["Authorization"] = "Bearer secret" },
            Attribution = "测试版权"
        };
        service.Current.MapSources.Add(custom);
        service.Current.DefaultMapSourceId = custom.Id;
        await service.SaveAsync();

        var reloaded = Create(directory.Path);
        await reloaded.LoadAsync();

        var actual = Assert.Single(reloaded.Current.MapSources, source => source.Id == custom.Id);
        Assert.Equal("Bearer secret", actual.Headers["Authorization"]);
        Assert.Equal(custom.Id, reloaded.Current.DefaultMapSourceId);
    }

    [Fact]
    public async Task NewSettings_UseOsmGermany_AndKeepExistingCustomDefault()
    {
        using var directory = new TemporaryDirectory();
        var service = Create(directory.Path);
        await service.LoadAsync();
        Assert.Equal(GlobalSettingsService.OsmGermanySourceId, service.Current.DefaultMapSourceId);
        var germany = Assert.Single(service.Current.MapSources,
            source => source.Id == GlobalSettingsService.OsmGermanySourceId);
        Assert.Equal("OSM Germany", germany.Name);
        Assert.Equal("https://tile.openstreetmap.de/{zoom}/{x}/{y}.png", germany.UrlTemplate);
        var custom = new MapSourceDefinition { Name = "已有图源", UrlTemplate = "https://example.org/{z}/{x}/{y}.png" };
        service.Current.MapSources.Add(custom);
        service.Current.DefaultMapSourceId = custom.Id;
        await service.SaveAsync();
        var reopened = Create(directory.Path);
        await reopened.LoadAsync();
        Assert.Equal(custom.Id, reopened.Current.DefaultMapSourceId);
    }

    [Fact]
    public async Task Load_CorruptJsonUsesSafeDefault()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "settings.json"), "{broken");
        var logging = new TestLoggingService();
        var service = Create(directory.Path, logging);

        await service.LoadAsync();

        Assert.NotEmpty(service.Current.MapSources);
        Assert.Contains(logging.Warnings, message => message.Contains("默认设置", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_AllSourcesDisabledCreatesEnabledFallback()
    {
        using var directory = new TemporaryDirectory();
        var service = Create(directory.Path);
        await service.LoadAsync();
        foreach (var source in service.Current.MapSources) source.Enabled = false;

        await service.SaveAsync();

        Assert.Contains(service.Current.MapSources, source => source.Enabled);
        Assert.NotNull(service.ResolveMapSource(null, out _));
    }

    [Fact]
    public async Task Resolve_MissingProjectSourceFallsBackWithoutChangingRequestedId()
    {
        using var directory = new TemporaryDirectory();
        var service = Create(directory.Path);
        await service.LoadAsync();
        var requested = Guid.NewGuid();

        var result = service.ResolveMapSource(requested, out var fallback);

        Assert.True(fallback);
        Assert.NotEqual(requested, result.Id);
    }

    private static GlobalSettingsService Create(string directory, ILoggingService? logging = null) =>
        new(new ApplicationDataPaths(directory), logging ?? new TestLoggingService(), new MapSourceTemplateService());

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HikingGuide.P4.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class TestLoggingService : ILoggingService
    {
        public List<string> Warnings { get; } = [];
        public void Debug(string message) { }
        public void Information(string message) { }
        public void Warning(string message) => Warnings.Add(message);
        public void Error(Exception exception, string message) { }
        public void Dispose() { }
    }
}

