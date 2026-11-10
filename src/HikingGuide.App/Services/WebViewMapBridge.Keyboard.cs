using System.Windows.Input;
using HikingGuide.Application.Maps;
namespace HikingGuide.App.Services;
public sealed partial class WebViewMapBridge
{
    private bool _mapKeyboardActive;
    private void OnMapPreviewKeyDown(object sender,KeyEventArgs e)
    {
        if(!_mapKeyboardActive||e.Handled||Keyboard.Modifiers!=ModifierKeys.None)return;
        var key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
        if(key is not (Key.W or Key.A or Key.S or Key.D))return;
        e.Handled=true;
        _=SendAsync(MapProtocol.SerializeCommand("PanMapKey",new{code="Key"+key}));
    }
}
