namespace HikingGuide.App.ViewModels;

public sealed partial class MapViewModel
{
    private readonly SemaphoreSlim _layerToggleGate=new(1,1);
    public bool DemContoursVisible {get=>_settings.Current.OfflineTerrain.ContoursVisible;set{if(value!=DemContoursVisible)_=SetQuickLayerAsync(0,value);}}
    public bool TerrainHillshadeVisible {get=>_settings.Current.OfflineTerrain.HillshadeVisible;set{if(value!=TerrainHillshadeVisible)_=SetQuickLayerAsync(1,value);}}
    public bool OsmRoadsVisible {get=>_settings.Current.OsmRoads.Enabled;set{if(value!=OsmRoadsVisible)_=SetQuickLayerAsync(2,value);}}
    private void NotifyQuickLayers()
    {OnPropertyChanged(nameof(DemContoursVisible));OnPropertyChanged(nameof(TerrainHillshadeVisible));OnPropertyChanged(nameof(OsmRoadsVisible));}
    private async Task SetQuickLayerAsync(int kind,bool visible)
    {
        await _layerToggleGate.WaitAsync();
        var previousTerrain=_settings.Current.OfflineTerrain;var previousRoads=_settings.Current.OsmRoads;
        try
        {
            if(kind==0)_settings.Current.OfflineTerrain=previousTerrain with{ContoursVisible=visible};
            else if(kind==1)_settings.Current.OfflineTerrain=previousTerrain with{HillshadeVisible=visible};
            else{var next=previousRoads.DeepCopy();next.Enabled=visible;_settings.Current.OsmRoads=next;}
            _settings.NotifyMapSettingsChanged();
            await _settings.SaveAsync();
        }
        catch(Exception error)
        {
            if(kind==0)_settings.Current.OfflineTerrain=_settings.Current.OfflineTerrain with{ContoursVisible=previousTerrain.ContoursVisible};
            else if(kind==1)_settings.Current.OfflineTerrain=_settings.Current.OfflineTerrain with{HillshadeVisible=previousTerrain.HillshadeVisible};
            else _settings.Current.OsmRoads.Enabled=previousRoads.Enabled;
            _log.Error(error,"保存地图快捷图层开关失败");_settings.NotifyMapSettingsChanged();
            UserMessage?.Invoke(this,"图层开关保存失败，请检查配置目录权限或磁盘空间。");
        }
        finally{NotifyQuickLayers();_layerToggleGate.Release();}
    }
}
