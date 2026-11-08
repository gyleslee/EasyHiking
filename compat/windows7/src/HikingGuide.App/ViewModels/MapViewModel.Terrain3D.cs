using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.App.ViewModels;

public sealed partial class MapViewModel
{
    private long _terrainRevision;
    private MapEventMessage? _reloadTerrainView;
    private bool _terrainCameraInitialized;
    private CancellationTokenSource? _terrainCameraSave;
    private void RememberTerrainView(MapEventMessage view)
    {
        if(view.Latitude is not {} lat||view.Longitude is not {} lon||view.Zoom is not {} zoom)return;
        _settings.Current.TerrainCamera=new(lat,lon,zoom,view.Bearing??0,view.Pitch??0);
        _terrainCameraSave?.Cancel();var stop=_terrainCameraSave=new();_ = SaveTerrainCameraAsync(stop.Token);
    }
    private async Task SaveTerrainCameraAsync(CancellationToken token)
    {try{await Task.Delay(500,token);await _settings.SaveAsync();}catch(OperationCanceledException){}catch(Exception e){_log.Error(e,"保存全局3D视角失败");}}
    private async Task SynchronizeTerrainReadyAsync()
    {
        var view=_reloadTerrainView;_reloadTerrainView=null;
        await SynchronizeAllAsync();
        if(view is not null&&_bridge is not null)await _bridge.SendAsync(MapProtocol.SerializeCommand("SetView",new{
            longitude=view.Longitude,latitude=view.Latitude,zoom=view.Zoom,bearing=view.Bearing,pitch=view.Pitch,
            suppressPersistence=true,restoreTerrainCamera=true},_project?.Id,_revision));
    }
    public async Task ClearTerrainCacheAsync()
    {
        if(_bridge is not null)await _bridge.SendAsync(MapProtocol.SerializeCommand("PauseTerrain",new{}));
        if(((App)System.Windows.Application.Current).TerrainTiles is {} generator)await generator.ClearAsync();
        await ApplyGlobalMapLayersAsync(true);
    }
    public Task ClearOfflineTerrainCacheAsync(bool contours)=>_bridge?.ClearOfflineTerrainCacheAsync(contours)??
        HikingGuide.Infrastructure.Maps.MapTileDiskCache.Shared(Services.DemTerrainTiles.CacheRoot).ClearAsync(contours?Services.DemTerrainTiles.ContourCacheId:Services.DemTerrainTiles.HillshadeCacheId);
    private bool _effectiveTerrainState;
    internal int TerrainBlockedEditCount {get;private set;}
    private DateTimeOffset _lastTerrainEditWarning;
    public bool TerrainCoordinateEditingBlocked=>EffectiveTerrainState||_settings.Current.Terrain3D?.Enabled==true;
    public bool EffectiveTerrainState {get=>_effectiveTerrainState;private set{if(SetField(ref _effectiveTerrainState,value)){OnPropertyChanged(nameof(TerrainToggleLabel));OnPropertyChanged(nameof(TerrainCoordinateEditingBlocked));}}}
    public string TerrainToggleLabel=>EffectiveTerrainState?"2D":"3D";
    public const string TerrainEditingWarning="当前为3D地形模式，该编辑操作需要精确地图坐标，请先切换到2D地图。";
    public async Task ToggleTerrainAsync()
    {
        var settings=_settings.Current.Terrain3D??new();
        if(settings.Enabled)settings=settings with{Enabled=false};
        else
        {
            var generator=((App)System.Windows.Application.Current).TerrainTiles;
            if(generator is null){UserMessage?.Invoke(this,"DEM运行库不可用，请先检查DEM管理。");return;}
            var choices=await generator.Sampler.AvailableAsync();
            if(choices.Count==0){UserMessage?.Invoke(this,"没有可采样的DEM，请先在DEM管理挂载并扫描数据库。");return;}
            if(settings.TerrainDemDatabaseId is {} previousId&&choices.All(c=>c.Id!=previousId))
            {UserMessage?.Invoke(this,"当前3D地形DEM未连接，请恢复该DEM，或在3D地图设置中明确选择其他DEM。");return;}
            var selected=choices.FirstOrDefault(c=>c.Id==settings.TerrainDemDatabaseId)??
                choices.FirstOrDefault(c=>c.Id==_settings.Current.OfflineTerrain.DemDatabaseId)??
                choices.FirstOrDefault(c=>c.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase))??choices[0];
            settings=settings with{Enabled=true,TerrainDemDatabaseId=selected.Id};
        }
        await SaveTerrainSettingsAsync(settings);
    }
    public async Task SaveTerrainSettingsAsync(Terrain3DSettings settings)
    {
        if(!settings.IsValid)throw new ArgumentException("3D地图参数无效。");
        settings=settings with{Exaggeration=Terrain3DSettings.RoundExaggeration(settings.Exaggeration)};
        _settings.Current.Terrain3D=settings;
        OnPropertyChanged(nameof(TerrainCoordinateEditingBlocked));
        _viewCancellation?.Cancel();
        await _settings.SaveAsync();_settings.NotifyMapSettingsChanged();
    }
    public Task SetTerrainCameraAsync(bool northUp)
    {
        if(!northUp&&_settings.Current.Terrain3D?.Enabled!=true){MapStatusText="请先启用3D地形后再恢复默认3D视角。";return Task.CompletedTask;}
        return _bridge?.SendAsync(MapProtocol.SerializeCommand("SetTerrainCamera",new{northUp,pitch=(_settings.Current.Terrain3D??new()).DefaultPitch}))??Task.CompletedTask;
    }
    private bool HandleTerrainEvent(MapEventMessage e)
    {
        if(TerrainCoordinateEditingBlocked&&e.Type is "FavoriteDragStarted" or "FavoriteDragCompleted" or "WaypointDragEnded" or "GuideNodeDragEnded" or "TrackPointDragEnded" or "TrackEditLineClicked" or "TrackSnapLocationPicked" or "MarkerAddLocationPicked" or "RoutePlanningPointPicked")
            e=e with{Type="TerrainEditBlocked"};
        if(e.Type=="TerrainEditBlocked"){TerrainBlockedEditCount++;_lastTerrainEditWarning=DateTimeOffset.UtcNow;MapStatusText=TerrainEditingWarning;return true;}
        if(e.Type is not ("TerrainStateChanged" or "TerrainUnavailable"))return false;
        if(e.RequestId!=_terrainRevision)return true;
        if(e.Type=="TerrainUnavailable")
        {EffectiveTerrainState=false;MapStatusText=e.Message??"当前3D地形DEM未连接，地图已切回2D。";_ = DisableUnavailableTerrainAsync(MapStatusText);}
        else
        {var wasEffective=EffectiveTerrainState;EffectiveTerrainState=e.Message=="active";if(e.Message=="loading")MapStatusText="正在生成3D地形…";else if((e.Message!="inactive"||wasEffective)&&DateTimeOffset.UtcNow-_lastTerrainEditWarning>TimeSpan.FromSeconds(3))MapStatusText=EffectiveTerrainState?"3D地形已就绪":"已切换为2D地图";}
        return true;
    }
    private async Task DisableUnavailableTerrainAsync(string message)
    {
        try{await SaveTerrainSettingsAsync((_settings.Current.Terrain3D??new()) with{Enabled=false});}
        catch(Exception error){_log.Error(error,"保存3D安全回退状态失败");}
        MapStatusText=message;
    }
    public bool AllowTerrainCoordinateEdit()
    {if(!TerrainCoordinateEditingBlocked)return true;MapStatusText=TerrainEditingWarning;UserMessage?.Invoke(this,TerrainEditingWarning);return false;}
}
