using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.App;

public sealed class TrackDisplaySettingsWindow:Window
{
    private readonly IGlobalSettingsService _settings;
    private readonly TabControl _tabs=new();
    private readonly TextBlock _status=new(){Margin=new(0,8,0,0),TextWrapping=TextWrapping.Wrap};
    private readonly Button _apply=new(){Content="应用并保存",Width=115,Margin=new(10,0,0,0)};
    private readonly List<StyleEditor> _editors=[];
    private string _selectedColor="#FFCF33";
    public TrackDisplaySettingsWindow(IGlobalSettingsService settings)
    {
        _settings=settings;Title="轨迹显示";Width=740;Height=710;MinWidth=640;MinHeight=570;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new(18)};Content=root;
        var intro=new TextBlock{Text="全局保存并应用到所有项目。选中样式叠加在原轨迹上；起终点优先使用选中设置。主攻略和普通轨迹沿用各条轨迹已有颜色。",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12)};
        DockPanel.SetDock(intro,Dock.Top);root.Children.Add(intro);
        var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
        bottom.Children.Add(_status);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,12,0,0)};bottom.Children.Add(buttons);
        var reset=new Button{Content="恢复默认样式",Width=115};reset.Click+=(_,_)=>{Load(new());_status.Text="已恢复面板默认值，点击“应用并保存”后生效。";};buttons.Children.Add(reset);
        _apply.Click+=async(_,_)=>await SaveAsync();buttons.Children.Add(_apply);
        buttons.Children.Add(new Button{Content="关闭",Width=85,Margin=new(10,0,0,0),IsCancel=true});
        root.Children.Add(_tabs);Load(TrackDisplaySettings.Normalize(settings.Current.TrackDisplay));
    }
    private void Load(TrackDisplaySettings value)
    {
        var index=_tabs.SelectedIndex;_selectedColor=value.SelectedColor;_tabs.Items.Clear();_editors.Clear();
        foreach(var (title,style,selected,color) in new[]{("选中轨迹",value.Selected,true,value.SelectedColor),("主攻略轨迹",value.MainGuide,false,"#2877C7"),("普通轨迹",value.Ordinary,false,"#39A070")})
        {
            var editor=new StyleEditor(title,style,color,selected?new Action<string>(c=>_selectedColor=c):null,this,selected?value.MainGuide:null);
            _editors.Add(editor);_tabs.Items.Add(new TabItem{Header=title,Content=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=editor.Panel}});
        }
        _tabs.SelectedIndex=Math.Max(0,index);
    }
    private async Task SaveAsync()
    {
        TrackDisplaySettings next;
        try{next=new(){SelectedColor=_selectedColor,Selected=_editors[0].Read(),MainGuide=_editors[1].Read(),Ordinary=_editors[2].Read()};}
        catch(ArgumentException e){MessageBox.Show(this,e.Message,"请检查轨迹显示设置",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
        var previous=_settings.Current.TrackDisplay;_apply.IsEnabled=false;
        try{_settings.Current.TrackDisplay=next;await _settings.SaveAsync();_status.Text="已应用并保存；各项目与2D、3D地图共用这套样式。";}
        catch(Exception e){_settings.Current.TrackDisplay=previous;MessageBox.Show(this,"保存失败："+e.Message,"轨迹显示",MessageBoxButton.OK,MessageBoxImage.Error);}
        finally{_apply.IsEnabled=true;}
    }
    private sealed class StyleEditor
    {
        public StackPanel Panel {get;}=new(){Margin=new(16)};
        private readonly string _name;
        private readonly TextBox _width=new(),_outline=new(),_size=new();
        private readonly Slider _transparency=new(){Minimum=0,Maximum=100,TickFrequency=1,IsSnapToTickEnabled=true,Width=170};
        private readonly ComboBox _line=new(),_endpoints=new(),_zoom=new();
        private readonly CheckBox _arrows=new(){Content="显示方向箭头"},_text=new(){Content="显示起终点文字"};
        private readonly Canvas _preview=new(){Height=75,Background=new SolidColorBrush(Color.FromRgb(240,244,247)),ClipToBounds=true};
        private string _color;
        private readonly TrackLineStyle? _underlay;
        private static readonly string[] Lines=["solid","dash","dot","dashdot"],Ends=["none","both","start","end"];
        public StyleEditor(string name,TrackLineStyle style,string color,Action<string>? changeColor,Window owner,TrackLineStyle? underlay=null)
        {
            _name=name;_color=color;_underlay=underlay;
            Panel.Children.Add(new TextBlock{Text="样式预览",FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,6)});Panel.Children.Add(_preview);
            Panel.Children.Add(new TextBlock{Text=changeColor is null?"预览使用示例颜色，地图继续使用各条轨迹自己的颜色。":"选中样式叠加在原轨迹上，虚线间隙透出原线条；此处以蓝色主攻略样式示意。",TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,14)});
            if(changeColor is not null)
            {
                var picker=new Button{Content=color,Width=210,HorizontalAlignment=HorizontalAlignment.Left};
                picker.Click+=(_,_)=>{var w=new TrackColorWindow(_color){Owner=owner,Title="选中轨迹颜色"};if(w.ShowDialog()==true){_color=w.SelectedColor;picker.Content=_color;changeColor(_color);Preview();}};
                Row("选中颜色",picker);
            }
            _width.Text=style.Width.ToString("0.#",CultureInfo.CurrentCulture);Row("线宽（像素）",_width,"1～20");
            _transparency.Value=(1-style.Opacity)*100;var opacity=new TextBlock{Width=45,Margin=new(8,0,0,0),VerticalAlignment=VerticalAlignment.Center};
            var slider=new StackPanel{Orientation=Orientation.Horizontal};slider.Children.Add(_transparency);slider.Children.Add(opacity);Row("透明度",slider);
            void SetPercent()=>opacity.Text=$"{_transparency.Value:0}%";SetPercent();_transparency.ValueChanged+=(_,_)=>{SetPercent();Preview();};
            _line.ItemsSource=new[]{"实线","虚线","点线","点划线"};_line.SelectedIndex=Array.IndexOf(Lines,style.LineStyle);Row("线条样式",_line);
            _outline.Text=style.OutlineWidth.ToString("0.#",CultureInfo.CurrentCulture);Row("描边宽度（像素）",_outline,"0～3，0为关闭");
            Row("方向指示",_arrows);_arrows.IsChecked=style.Arrows;
            _endpoints.ItemsSource=new[]{"不显示","起点和终点","仅起点","仅终点"};_endpoints.SelectedIndex=Array.IndexOf(Ends,style.Endpoints);Row("起终点标识",_endpoints);
            _size.Text=style.EndpointSize.ToString("0.#",CultureInfo.CurrentCulture);Row("标识半径（像素）",_size,"4～14");
            _zoom.ItemsSource=Enumerable.Range(0,23).Select(z=>$"Z{z}");_zoom.SelectedIndex=style.EndpointMinZoom;Row("标识最小显示层级",_zoom);
            _text.IsChecked=style.ShowEndpointText;Row("标识文字",_text);
            Panel.Children.Add(new TextBlock{Text="透明度0%为完全显示，100%为完全透明。描边自动使用黑／白对比色。起点为蓝、终点为绿，起／终文字在圆点中央；重合起终点合并显示。",TextWrapping=TextWrapping.Wrap,Margin=new(0,10,0,0)});
            foreach(var box in new[]{_width,_outline,_size})box.TextChanged+=(_,_)=>Preview();
            foreach(var combo in new[]{_line,_endpoints,_zoom})combo.SelectionChanged+=(_,_)=>Preview();
            foreach(var box in new[]{_arrows,_text}){box.Checked+=(_,_)=>Preview();box.Unchecked+=(_,_)=>Preview();}
            _preview.SizeChanged+=(_,_)=>Preview();Preview();
        }
        private void Row(string title,UIElement control,string? hint=null)
        {
            var grid=new Grid{Margin=new(0,0,0,10)};grid.ColumnDefinitions.Add(new(){Width=new(160)});grid.ColumnDefinitions.Add(new(){Width=new(230)});grid.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
            grid.Children.Add(new TextBlock{Text=title,VerticalAlignment=VerticalAlignment.Center});Grid.SetColumn(control,1);grid.Children.Add(control);
            if(control is Control c){c.MinHeight=26;c.VerticalContentAlignment=VerticalAlignment.Center;}
            if(hint is not null){var note=new TextBlock{Text=hint,Margin=new(8,0,0,0),VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap};Grid.SetColumn(note,2);grid.Children.Add(note);}Panel.Children.Add(grid);
        }
        public TrackLineStyle Read()
        {
            double Number(TextBox box,string field,double min,double max)
            {
                if((double.TryParse(box.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var n)||double.TryParse(box.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out n))&&double.IsFinite(n)&&n>=min&&n<=max)return n;
                throw new ArgumentException($"{_name}：{field}请输入{min}～{max}之间的数字。");
            }
            return new(){Width=Number(_width,"线宽",1,20),Opacity=1-_transparency.Value/100,OutlineWidth=Number(_outline,"描边宽度",0,3),
                EndpointSize=Number(_size,"标识半径",4,14),EndpointMinZoom=Math.Max(0,_zoom.SelectedIndex),LineStyle=Lines[Math.Max(0,_line.SelectedIndex)],
                Endpoints=Ends[Math.Max(0,_endpoints.SelectedIndex)],Arrows=_arrows.IsChecked==true,ShowEndpointText=_text.IsChecked==true};
        }
        private void Preview()
        {
            TrackLineStyle s;try{s=Read();}catch(ArgumentException){return;}
            _preview.Children.Clear();var width=Math.Max(250,_preview.ActualWidth);var brush=(Brush)new BrushConverter().ConvertFromString(_color)!;
            var rgb=(Color)ColorConverter.ConvertFromString(_color);var outline=rgb.R*.299+rgb.G*.587+rgb.B*.114>140?Brushes.Black:Brushes.White;
            if(_underlay is {} below)Line(new SolidColorBrush(Color.FromRgb(25,92,173)),below.Width,below);
            if(s.OutlineWidth>0)foreach(var side in new[]{-1,1})_preview.Children.Add(new Line{X1=28,X2=width-28,Y1=35+side*(s.Width+s.OutlineWidth)/2,Y2=35+side*(s.Width+s.OutlineWidth)/2,Stroke=outline,StrokeThickness=s.OutlineWidth,Opacity=s.Opacity});
            Line(brush,s.Width);
            if(s.Arrows)
            {
                var arrows=new Canvas{Opacity=s.Opacity};_preview.Children.Add(arrows);
                double Linear(byte v){var x=v/255d;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}
                var luminance=Linear(rgb.R)*.2126+Linear(rgb.G)*.7152+Linear(rgb.B)*.0722;
                var dark=(luminance+.05)/(Linear(17)+.05)>=1.05/(luminance+.05);
                var ink=dark?new SolidColorBrush(Color.FromRgb(17,17,17)):Brushes.White;
                var edge=dark?Brushes.White:new SolidColorBrush(Color.FromRgb(17,17,17));
                var height=Math.Max(10,Math.Max(s.Width,_underlay?.Width??0)+4);var half=height/2;var span=height*.65;var band=height*.32;var gap=height*.18;var border=Math.Min(.9,height*.075);
                foreach(var center in new[]{-(span+gap)/2,(span+gap)/2})
                {
                    var left=center-span/2;var right=center+span/2;
                    Point[] polygon=[new(left,-half),new(left+band,-half),new(right,0),new(left+band,half),new(left,half),new(right-band,0)];
                    Paint(polygon,edge);var inside=new Point[polygon.Length];
                    for(var i=0;i<polygon.Length;i++)
                    {
                        var before=polygon[(i+polygon.Length-1)%polygon.Length];var at=polygon[i];var after=polygon[(i+1)%polygon.Length];
                        var u=at-before;var v=after-at;var a=new Point(at.X-u.Y/u.Length*border,at.Y+u.X/u.Length*border);var b=new Point(at.X-v.Y/v.Length*border,at.Y+v.X/v.Length*border);
                        var cross=u.X*v.Y-u.Y*v.X;var t=Math.Abs(cross)<1e-12?0:((b.X-a.X)*v.Y-(b.Y-a.Y)*v.X)/cross;
                        inside[i]=a+u*t;
                    }
                    Paint(inside,ink);
                }
                void Paint(IEnumerable<Point> points,Brush fill)=>arrows.Children.Add(new Polygon{Points=new PointCollection(points.Select(p=>new Point(width/2+p.X,35+p.Y))),Fill=fill});
            }
            if(s.Endpoints is "both" or "start")Endpoint(28,new SolidColorBrush(Color.FromRgb(37,99,235)),"起");
            if(s.Endpoints is "both" or "end")Endpoint(width-28,new SolidColorBrush(Color.FromRgb(22,163,74)),"终");
            void Line(Brush b,double thickness,TrackLineStyle? style=null){var paint=style??s;var line=new Line{X1=28,Y1=35,X2=width-28,Y2=35,Stroke=b,StrokeThickness=thickness,Opacity=paint.Opacity,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round};if(paint.LineStyle!="solid")line.StrokeDashArray=paint.LineStyle switch{"dash"=>new([3,2]),"dot"=>new([.2,2]),_=>new([3,2,.2,2])};_preview.Children.Add(line);}
            void Endpoint(double x,Brush b,string text)
            {
                var marker=new Grid{Width=s.EndpointSize*2,Height=s.EndpointSize*2,Opacity=s.Opacity};
                marker.Children.Add(new Ellipse{Fill=b,Stroke=Brushes.White,StrokeThickness=1.5});
                if(s.ShowEndpointText)marker.Children.Add(new TextBlock{Text=text,FontSize=Math.Max(6,s.EndpointSize*1.3),Foreground=Brushes.White,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,TextAlignment=TextAlignment.Center});
                Canvas.SetLeft(marker,x-s.EndpointSize);Canvas.SetTop(marker,35-s.EndpointSize);_preview.Children.Add(marker);
            }
        }
    }
}
