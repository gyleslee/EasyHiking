using System.Windows;
using System.Windows.Controls;
using HikingGuide.App.ViewModels;
namespace HikingGuide.App;
public partial class MainWindow
{
    private void OnMarkerDisplayMenuOpened(object sender,RoutedEventArgs e)
    {
        if(sender is not MenuItem menu||DataContext is not MainViewModel main)return;
        foreach(var item in menu.Items.OfType<MenuItem>())item.IsChecked=int.TryParse(item.Tag?.ToString(),out var mode)&&mode==main.Map.MarkerDisplayMode;
    }
    private void OnMarkerDisplayModeClick(object sender,RoutedEventArgs e)
    {
        if(sender is not MenuItem item||DataContext is not MainViewModel main||!int.TryParse(item.Tag?.ToString(),out var mode))return;
        main.Map.MarkerDisplayMode=mode;
        if(ItemsControl.ItemsControlFromItemContainer(item) is MenuItem parent)OnMarkerDisplayMenuOpened(parent,e);
        e.Handled=true;
    }
}
