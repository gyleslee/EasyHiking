using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

public interface IMapBridge
{
    bool IsReady { get; }
    event EventHandler<MapEventMessage>? EventReceived;
    event EventHandler<string>? InitializationFailed;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task ReloadAsync(CancellationToken cancellationToken = default);
    Task SendAsync(string commandJson, CancellationToken cancellationToken = default);
    Task CapturePreviewAsync(string outputPath, CancellationToken cancellationToken = default);
    void SetActiveMapSource(MapSourceDefinition? source);
    IReadOnlyList<RasterLayerSlot> ConfigureRasterLayers(IReadOnlyList<RasterLayerSlot> slots) => slots;
    object ConfigureOfflineTerrain(OfflineTerrainSettings settings) => new { contours=false,hillshade=false };
    Task ClearOfflineTerrainCacheAsync(bool contours)=>Task.CompletedTask;
    TerrainMapConfiguration ConfigureTerrain3D(Terrain3DSettings settings) => new();
    Task<HikingGuide.Core.Maps.OfflineMapRegion?> SelectOfflineRegionAsync(CancellationToken token=default)=>Task.FromResult<HikingGuide.Core.Maps.OfflineMapRegion?>(null);
}
