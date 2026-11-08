using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HikingGuide.Core.Models;

namespace HikingGuide.App;

/// <summary>只读主题Z16线条示例，与地图使用同一份导入的主题线条。</summary>
public sealed class OsmRoadStylePreview:FrameworkElement
{
    public static readonly DependencyProperty RoadTypeIdProperty=DependencyProperty.Register(nameof(RoadTypeId),typeof(string),typeof(OsmRoadStylePreview),new FrameworkPropertyMetadata("",FrameworkPropertyMetadataOptions.AffectsRender));
    public string RoadTypeId {get=>(string)GetValue(RoadTypeIdProperty);set=>SetValue(RoadTypeIdProperty,value);}
    private static readonly ConcurrentDictionary<string,DrawingGroup> Examples=new();
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        context.DrawRectangle(Brushes.White,new Pen(Brushes.LightGray,.5),new Rect(0,1,ActualWidth,Math.Max(0,ActualHeight-2)));
        var drawing=Examples.GetOrAdd(RoadTypeId,Create);
        context.PushTransform(new ScaleTransform(ActualWidth/170,ActualHeight/30));context.DrawDrawing(drawing);context.Pop();context.Pop();
    }
    private static DrawingGroup Create(string id)
    {
        var result=new DrawingGroup();using(var draw=result.Open())
        foreach(var stroke in OsmRoadDefaults.PreviewStrokes(id))
        {
            var scale=stroke.ScaleMode=="none"?1:Math.Pow(1.5,4);var width=stroke.Width*scale;
            var rgb=(Color)ColorConverter.ConvertFromString(stroke.Color);rgb.A=(byte)Math.Round(stroke.Alpha*255);
            var cap=stroke.LineCap=="round"?PenLineCap.Round:stroke.LineCap=="square"?PenLineCap.Square:PenLineCap.Flat;
            var pen=new Pen(new SolidColorBrush(rgb),width){StartLineCap=cap,EndLineCap=cap,DashCap=cap,LineJoin=PenLineJoin.Round};
            if(!string.IsNullOrWhiteSpace(stroke.DashArray))
            {var dashScale=stroke.ScaleMode=="all"?scale:1;pen.DashStyle=new DashStyle(stroke.DashArray.Split(',').Select(v=>double.Parse(v,CultureInfo.InvariantCulture)*dashScale/Math.Max(.01,width)),0);}
            draw.DrawLine(pen,new Point(8,15),new Point(162,15));
        }
        result.Freeze();return result;
    }
}
