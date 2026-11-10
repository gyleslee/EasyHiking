using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HikingGuide.Core.Favorites;
using HikingGuide.Infrastructure.Favorites;
using HikingGuide.App.Services;
namespace HikingGuide.App;

public sealed record FavoriteImageViewItem(string Name,Func<CancellationToken,Task<BitmapSource>> Load);
public sealed class FavoriteImageViewerWindow:Window
{
    private readonly IReadOnlyList<FavoriteImageViewItem> _items;
    private readonly ComboBox _choice=new(){DisplayMemberPath="Name",MinWidth=150,MaxWidth=360};
    private readonly Image _photo=new(){Stretch=Stretch.None};
    private readonly ScrollViewer _scroll=new(){HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=Brushes.DimGray};
    private readonly TextBlock _status=new(){Margin=new Thickness(8),TextWrapping=TextWrapping.Wrap};
    private CancellationTokenSource? _request;private double _zoom=1;private int _rotation;private Point? _drag;private Point _offset;
    public FavoriteImageViewerWindow(IReadOnlyList<FavoriteImageViewItem> items,int selected=0)
    {
        _items=items;Title="收藏点图片";Width=950;Height=720;MinWidth=540;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel();Content=root;var tools=new WrapPanel{Margin=new Thickness(4)};DockPanel.SetDock(tools,Dock.Top);root.Children.Add(tools);
        void Action(string title,Action action)=>tools.Children.Add(FavoriteEditorWindow.Button(title,()=>{action();return Task.CompletedTask;}));
        Action("上一张",()=>Move(-1));Action("下一张",()=>Move(1));tools.Children.Add(_choice);Action("放大",()=>Zoom(1.25));Action("缩小",()=>Zoom(.8));Action("左转90°",()=>Rotate(-90));Action("右转90°",()=>Rotate(90));Action("适应窗口",Fit);Action("实际大小",()=>{_zoom=1;Transform();});
        DockPanel.SetDock(_status,Dock.Bottom);root.Children.Add(_status);root.Children.Add(_scroll);_scroll.Content=_photo;
        _choice.ItemsSource=items;_choice.SelectionChanged+=async(_,_)=>await ShowAsync();Loaded+=(_,_)=>{if(items.Count>0)_choice.SelectedIndex=Math.Clamp(selected,0,items.Count-1);else _status.Text="此收藏点没有图片。";};Closed+=(_,_)=>_request?.Cancel();
        _scroll.PreviewMouseWheel+=(_,e)=>{Zoom(e.Delta>0?1.2:1/1.2);e.Handled=true;};
        _photo.MouseLeftButtonDown+=(_,e)=>{if(e.ClickCount==2){Fit();return;}_drag=e.GetPosition(_scroll);_offset=new(_scroll.HorizontalOffset,_scroll.VerticalOffset);_photo.CaptureMouse();};
        _photo.MouseMove+=(_,e)=>{if(_drag is Point start){var p=e.GetPosition(_scroll);_scroll.ScrollToHorizontalOffset(_offset.X+start.X-p.X);_scroll.ScrollToVerticalOffset(_offset.Y+start.Y-p.Y);}};
        _photo.MouseLeftButtonUp+=(_,_)=>{_drag=null;_photo.ReleaseMouseCapture();};_photo.LostMouseCapture+=(_,_)=>_drag=null;
    }
    private void Move(int step){if(_items.Count>0)_choice.SelectedIndex=(_choice.SelectedIndex+step+_items.Count)%_items.Count;}
    private async Task ShowAsync(){_request?.Cancel();_request?.Dispose();_request=new();var token=_request.Token;var i=_choice.SelectedIndex;if(i<0)return;_status.Text="正在读取图片…";_photo.Source=null;try{var b=await _items[i].Load(token);if(token.IsCancellationRequested)return;_photo.Source=b;_photo.Width=b.PixelWidth;_photo.Height=b.PixelHeight;_rotation=0;Fit();Title=$"收藏点图片｜{i+1}/{_items.Count}｜{_items[i].Name}";}catch(OperationCanceledException){}catch(Exception e){if(!token.IsCancellationRequested)_status.Text="图片读取失败："+e.Message;}}
    private void Fit(){if(_photo.Source is not BitmapSource b)return;var turn=_rotation%180!=0;_zoom=Math.Clamp(Math.Min(Math.Max(100,_scroll.ActualWidth-28)/(turn?b.PixelHeight:b.PixelWidth),Math.Max(100,_scroll.ActualHeight-28)/(turn?b.PixelWidth:b.PixelHeight)),.01,16);Transform();}
    private void Zoom(double factor){if(_photo.Source is null)return;var x=_scroll.HorizontalOffset+_scroll.ViewportWidth/2;var y=_scroll.VerticalOffset+_scroll.ViewportHeight/2;var old=_zoom;_zoom=Math.Clamp(_zoom*factor,.01,32);Transform();_scroll.UpdateLayout();_scroll.ScrollToHorizontalOffset(x*_zoom/old-_scroll.ViewportWidth/2);_scroll.ScrollToVerticalOffset(y*_zoom/old-_scroll.ViewportHeight/2);}
    private void Rotate(int angle){_rotation=(_rotation+angle+360)%360;Fit();}
    private void Transform(){var g=new TransformGroup();g.Children.Add(new RotateTransform(_rotation));g.Children.Add(new ScaleTransform(_zoom,_zoom));_photo.LayoutTransform=g;_status.Text=$"缩放 {_zoom:P0} · 旋转 {_rotation}° · 滚轮缩放，按住图片拖动，双击适应窗口";}
    internal double ZoomForAutomation=>_zoom;internal int RotationForAutomation=>_rotation;
    internal bool PhotoReadyForAutomation=>_photo.Source is not null;
    internal void ZoomForTest(double scale)=>Zoom(scale);internal void RotateForTest(int degrees)=>Rotate(degrees);
}
internal static class FavoriteImageEntries
{
    public static IReadOnlyList<FavoriteImageViewItem> Create(FavoritesRepository repo,FavoriteDetails? details,IReadOnlyList<FavoriteMediaAttachment> retained,IReadOnlyList<FavoriteMediaInput>? added=null){
        var items=retained.Select(a=>new FavoriteImageViewItem(a.OriginalFileName+(a.Caption is{Length:>0}?" · "+a.Caption:""),async token=>{
            byte[] bytes;if(a.MediaBlobId is Guid id&&details?.Blobs.FirstOrDefault(b=>b.Id==id) is{} blob)bytes=await repo.ReadOriginalAsync(blob,token);
            else if(!string.IsNullOrWhiteSpace(a.ExternalUrl))bytes=(await FavoriteImageService.DownloadAsync(a.ExternalUrl,token)).Bytes;
            else throw new InvalidOperationException("此图片暂无可用原图。");return await Task.Run(()=>FavoriteImageService.Decode(bytes),token);
        })).ToList();items.AddRange((added??[]).Select(a=>new FavoriteImageViewItem(a.OriginalFileName+(a.Caption is{Length:>0}?" · "+a.Caption:""),token=>Task.Run(()=>FavoriteImageService.Decode(a.Bytes),token))));return items;
    }
}
