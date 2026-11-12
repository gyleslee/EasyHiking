using System.Text.Json;
using System.Text.Json.Serialization;
using HikingGuide.Application.Logging;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.Infrastructure.Maps;

public sealed class GlobalSettingsService(
    ApplicationDataPaths paths,
    ILoggingService logging,
    MapSourceTemplateService templateService,
    string? defaultsDirectory=null) : IGlobalSettingsService
{
    public static readonly Guid OpenStreetMapSourceId = Guid.Parse("6c785f14-4c4f-45af-9e70-d8aa7af5f76c");
    public static readonly Guid OsmGermanySourceId = Guid.Parse("90a2fae7-7063-4938-912e-4f85b31c650c");
    public static readonly Guid HumanitarianSourceId = Guid.Parse("e2c96a80-a6d3-438d-a3b8-4f923e27027f");
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GlobalSettings Current { get; private set; } = CreateDefaults();
    public string SettingsFilePath => Path.Combine(paths.RootDirectory, "settings.json");
    public string? MapSettingsWarning { get; private set; }
    public event EventHandler? MapSettingsChanged;
    public void NotifyMapSettingsChanged() => MapSettingsChanged?.Invoke(this,EventArgs.Empty);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var readPath = SettingsFilePath;
            var usePackagedDefaults=!File.Exists(readPath);
            if (!File.Exists(readPath) && ApplicationDataPaths.IsInstalledApplication(AppContext.BaseDirectory))
                readPath = Path.Combine(AppContext.BaseDirectory, "Defaults", "settings.json");
            if (!File.Exists(readPath))
            {
                Current = CreateDefaults();
                await LoadPackagedRoadLevelsAsync(cancellationToken);
                return;
            }
            try
            {
                await using var input = new FileStream(readPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                Current = await JsonSerializer.DeserializeAsync<GlobalSettings>(input, JsonOptions, cancellationToken) ?? CreateDefaults();
                Normalize(Current);
                // 旧安装载荷可能仍带20%的种子；只更新新用户默认，不覆盖已有个人设置。
                if(usePackagedDefaults)Current.OfflineTerrain=Current.OfflineTerrain with{HillshadeOpacity=new OfflineTerrainSettings().HillshadeOpacity};
                if(usePackagedDefaults)await LoadPackagedRoadLevelsAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                MapSettingsWarning = "全局设置文件损坏或不可读取，已使用默认设置；原文件未删除。";
                logging.Warning(MapSettingsWarning);
                Current = CreateDefaults();
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Normalize(Current);
            Current.OsmRoads.RememberDisplayLevels();
            Directory.CreateDirectory(paths.RootDirectory);
            var tempPath = SettingsFilePath + ".tmp";
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                await JsonSerializer.SerializeAsync(output, Current, JsonOptions, cancellationToken);
            File.Move(tempPath, SettingsFilePath, true);
        }
        finally { _gate.Release(); }
        NotifyMapSettingsChanged();
    }

    public MapSourceDefinition ResolveMapSource(Guid? requestedId, out bool usedFallback)
    {
        var enabled = Current.MapSources.Where(source => source.Enabled && templateService.Validate(source).IsValid).ToArray();
        var selected = requestedId.HasValue ? enabled.FirstOrDefault(source => source.Id == requestedId.Value) : null;
        selected ??= Current.DefaultMapSourceId.HasValue
            ? enabled.FirstOrDefault(source => source.Id == Current.DefaultMapSourceId.Value)
            : null;
        selected ??= enabled.FirstOrDefault();
        if (selected is null)
        {
            selected = CreateDefaultSource();
            Current.MapSources.Add(selected);
        }
        usedFallback = requestedId.HasValue && selected.Id != requestedId.Value;
        return selected;
    }

    private async Task LoadPackagedRoadLevelsAsync(CancellationToken cancellationToken)
    {
        var file=Path.Combine(defaultsDirectory??Path.Combine(AppContext.BaseDirectory,"Defaults"),"osm-road-default-levels.json");
        if(!File.Exists(file))return;
        try
        {
            await using var input=File.OpenRead(file);
            var levels=await JsonSerializer.DeserializeAsync<Dictionary<string,OsmRoadZoomRange>>(input,JsonOptions,cancellationToken);
            if(levels is null||levels.Count>1000||levels.Any(p=>p.Value is not {IsValid:true}))throw new JsonException("道路默认层级无效。");
            Current.OsmRoads.DefaultZoomLevels=levels;
            // 仅新用户初始化时应用，已有用户的颜色和显示层级不受安装包默认值影响。
            foreach(var style in Current.OsmRoads.Styles)
                if(levels.TryGetValue(style.Id,out var range)){style.MinZoom=range.MinZoom;style.MaxZoom=range.MaxZoom;}
        }
        catch(Exception error) when(error is JsonException or IOException or UnauthorizedAccessException)
        {logging.Warning("道路默认层级文件无法读取，已使用内置层级；原文件未修改。");}
    }

    private void Normalize(GlobalSettings settings)
    {
        settings.OsmRoads??=new();
        if(!settings.OsmRoads.IsValid)
        {
            var road=settings.OsmRoads;var valid=road.Styles?.Where(s=>s is not null&&s.IsValid).GroupBy(s=>s.Id).Select(g=>g.First()).ToList();
            road.Styles=valid is {Count:>0}?valid:OsmRoadDefaults.CreateStyles();
            road.Opacity=double.IsFinite(road.Opacity)?Math.Clamp(road.Opacity,0,1):1;
            road.CacheCapacityMb=Math.Clamp(road.CacheCapacityMb,0,1048576);road.MinimumZoom=Math.Clamp(road.MinimumZoom,8,14);
        }
        settings.OsmRoads.UpgradeDisplayRules();
        settings.PanelVisibility ??= new();
        if(settings.OfflineTerrain is {} opacitySettings && (!double.IsFinite(opacitySettings.ContourOpacity) || opacitySettings.ContourOpacity is <0 or >1))
            settings.OfflineTerrain=opacitySettings with {ContourOpacity=1};
        if(settings.OfflineTerrain is {} offline&&offline.MajorContourIntervalMeters<=0)
            settings.OfflineTerrain=offline with{MajorContourIntervalMeters=50};
        if(settings.OfflineTerrain is not {IsValid:true})settings.OfflineTerrain=new();
        if(settings.OfflineTerrain.ScreenAlignedLabels)settings.OfflineTerrain=settings.OfflineTerrain with{ScreenAlignedLabels=false};
        if(settings.TerrainCamera is {IsValid:false})settings.TerrainCamera=null;
        if(settings.Terrain3D is not {IsValid:true})settings.Terrain3D=new()
        {ShowContours=settings.OfflineTerrain.DemDatabaseId.HasValue?settings.OfflineTerrain.ContoursVisible:true,
            ShowHillshade=settings.OfflineTerrain.DemDatabaseId.HasValue?settings.OfflineTerrain.HillshadeVisible:true};
        settings.Terrain3D=settings.Terrain3D with{Exaggeration=Terrain3DSettings.RoundExaggeration(settings.Terrain3D.Exaggeration)};
        if(settings.UnifiedMapDemVersion<1)
        {
            settings.Terrain3D=settings.Terrain3D with{TerrainDemDatabaseId=settings.Terrain3D.TerrainDemDatabaseId??settings.OfflineTerrain.DemDatabaseId};
            settings.UnifiedMapDemVersion=1;
        }
        settings.OfflineTerrain=settings.OfflineTerrain with{DemDatabaseId=settings.Terrain3D.TerrainDemDatabaseId};
        if (settings.PanelLayout is { IsValid: false }) settings.PanelLayout = null;
        settings.GuideTableColumnWidths = (settings.GuideTableColumnWidths ?? [])
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && double.IsFinite(pair.Value) && pair.Value >= 20 && pair.Value <= 4000)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (!double.IsFinite(settings.FlatSlopeThresholdPercent) || settings.FlatSlopeThresholdPercent < 0 || settings.FlatSlopeThresholdPercent > 100)
            settings.FlatSlopeThresholdPercent = new HikingGuide.Core.Elevation.LocusTerrainDistanceProfile().FlatSlopeThresholdPercent;
        settings.MapSources ??= [];
        settings.MapSources.RemoveAll(source => source is null || source.Id == Guid.Empty);
        foreach (var source in settings.MapSources) source.Headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach(var source in settings.MapSources) if(source.CacheCapacityMb<0 || source.CacheCapacityMb>1048576)source.CacheCapacityMb=500;
        foreach (var source in settings.MapSources) source.Usage ??= source.EffectiveUsage;
        var duplicateIds = settings.MapSources.GroupBy(source => source.Id).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        settings.MapSources.RemoveAll(source => duplicateIds.Contains(source.Id));
        var missingGermany=settings.MapSources.All(source=>source.Id!=OsmGermanySourceId);
        var existingBase=settings.MapSources.FirstOrDefault(source=>MapLayerStackService.IsAvailable(source,MapSourceUsage.Base));
        if(missingGermany && settings.MapLayerStack is {} oldStack && !settings.MapSources.Any(source=>source.Id==oldStack.BaseSourceId&&MapLayerStackService.IsAvailable(source,MapSourceUsage.Base)) && existingBase is not null)
        {
            settings.MapLayerStack=oldStack with{BaseSourceId=existingBase.Id};MapSettingsWarning="默认底图异常缺失，已回退到第一个有效底图；内置OSM Germany定义已恢复。";logging.Warning(MapSettingsWarning);
        }
        if (missingGermany)
            settings.MapSources.Add(CreateGermanySource());
        if (settings.MapLayerStack is null && settings.MapSources.All(source => source.Id != HumanitarianSourceId)) settings.MapSources.Add(CreateHumanitarianSource());
        if (!settings.MapSources.Any(source=>MapLayerStackService.IsAvailable(source,MapSourceUsage.Base)))
        {
            settings.MapSources.RemoveAll(source=>source.Id==OsmGermanySourceId); settings.MapSources.Add(CreateGermanySource());
        }
        if (settings.MapSources.All(source => !source.Enabled))
        {
            var fallback = CreateGermanySource();
            if (settings.MapSources.All(source => source.Id != fallback.Id))
                settings.MapSources.Add(fallback);
            else
                settings.MapSources.First(source => source.Id == fallback.Id).Enabled = true;
        }
        if (!settings.DefaultMapSourceId.HasValue || settings.DefaultMapSourceId == OpenStreetMapSourceId ||
            settings.MapSources.All(source => source.Id != settings.DefaultMapSourceId.Value))
            settings.DefaultMapSourceId = OsmGermanySourceId;
        var original = settings.MapLayerStack;
        settings.MapLayerStack = MapLayerStackService.Normalize(original,settings.MapSources);
        if(original is not null && original != settings.MapLayerStack)
        {
            MapSettingsWarning = "地图图层设置中存在不可用或重复图源，已安全恢复有效底图并清理无效叠加层。";
            logging.Warning(MapSettingsWarning);
        }
    }

    private static GlobalSettings CreateDefaults()
    {
        return new GlobalSettings { MapSources = [CreateGermanySource(), CreateDefaultSource(),CreateHumanitarianSource()],
            DefaultMapSourceId = OsmGermanySourceId,MapLayerStack = new() };
    }

    private static MapSourceDefinition CreateHumanitarianSource() => new()
    {
        Id=HumanitarianSourceId,Name="Humanitarian",UrlTemplate="https://{switch:a,b,c}.tile.openstreetmap.fr/hot/{zoom}/{x}/{y}.png",
        Usage=MapSourceUsage.Both,UserAgent="HikingGuide/0.10.1",Attribution="© OpenStreetMap contributors | Tiles style: Humanitarian OpenStreetMap Team; hosted by OpenStreetMap France"
    };

    private static MapSourceDefinition CreateGermanySource() => new()
    {
        Id = OsmGermanySourceId,
        Name = "OSM Germany",
        UrlTemplate = "https://tile.openstreetmap.de/{zoom}/{x}/{y}.png",
        MinZoom = 0,
        MaxZoom = 19,
        Opacity = 1,
        Enabled = true,
        UserAgent = "HikingGuide/0.4.0",
        Attribution = "© OpenStreetMap contributors"
    };

    private static MapSourceDefinition CreateDefaultSource() => new()
    {
        Id = OpenStreetMapSourceId,
        Name = "OpenStreetMap（基础测试）",
        UrlTemplate = "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
        MinZoom = 0,
        MaxZoom = 19,
        Opacity = 1,
        Enabled = true,
        UserAgent = "HikingGuide/0.4.0",
        Attribution = "© OpenStreetMap contributors"
    };

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
