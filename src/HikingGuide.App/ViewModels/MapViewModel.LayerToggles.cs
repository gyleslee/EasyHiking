namespace HikingGuide.App.ViewModels;

public sealed partial class MapViewModel
{
    public IReadOnlyList<string> MarkerDisplayModes {get;}=["仅圆点","文字","缩略图","文字与缩略图"];
    public int MarkerDisplayMode {get=>Math.Clamp(_settings.Current.MarkerDisplayMode,0,3);set{if(value<0||value>3||value==MarkerDisplayMode)return;_settings.Current.MarkerDisplayMode=value;OnPropertyChanged();_=SaveMarkerDisplayAsync();}}
    private async Task SaveMarkerDisplayAsync(){try{if(_bridge?.IsReady==true)await _bridge.SendAsync(HikingGuide.Application.Maps.MapProtocol.SerializeCommand("SetMarkerDisplay",new{mode=MarkerDisplayMode}));await _settings.SaveAsync();}catch(Exception error){_log.Error(error,"保存标注显示设置失败");UserMessage?.Invoke(this,"标注显示设置保存失败，请检查配置目录。");}}
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
