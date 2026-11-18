namespace HikingGuide.Core.Models;

/// <summary>软件全局的地图外观；不进入项目或修改原始轨迹。</summary>
public sealed record TrackDisplaySettings
{
    public string SelectedColor { get; init; } = "#FD9700";
    public TrackLineStyle Selected { get; init; } = new() { Width=8, Opacity=0.9, LineStyle="dashdot", OutlineWidth=1, Arrows=true, Endpoints="both", EndpointSize=7, EndpointMinZoom=9, ShowEndpointText=true };
    public TrackLineStyle MainGuide { get; init; } = new() { Width=7, Opacity=1, LineStyle="solid", OutlineWidth=0.5, Arrows=true, Endpoints="both", EndpointSize=6, EndpointMinZoom=11, ShowEndpointText=true };
    public TrackLineStyle Ordinary { get; init; } = new() { Width=5, Opacity=0.9, LineStyle="solid", OutlineWidth=0, Arrows=true, Endpoints="both", EndpointSize=5, EndpointMinZoom=12, ShowEndpointText=true };
    public static TrackDisplaySettings Normalize(TrackDisplaySettings? value)
    {
        var defaults=new TrackDisplaySettings();value??=defaults;
        var color=value.SelectedColor;
        var valid=color is {Length:7}&&color[0]=='#'&&color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF")<0;
        return value with {SelectedColor=valid?color!.ToUpperInvariant():defaults.SelectedColor,
            Selected=TrackLineStyle.Normalize(value.Selected,defaults.Selected),
            MainGuide=TrackLineStyle.Normalize(value.MainGuide,defaults.MainGuide),
            Ordinary=TrackLineStyle.Normalize(value.Ordinary,defaults.Ordinary)};
    }
}

public sealed record TrackLineStyle
{
    public double Width { get; init; } = 3;
    public double Opacity { get; init; } = .82;
    public string LineStyle { get; init; } = "solid";
    public double OutlineWidth { get; init; }
    public bool Arrows { get; init; } = true;
    public string Endpoints { get; init; } = "none";
    public double EndpointSize { get; init; } = 7;
    public int EndpointMinZoom { get; init; } = 12;
    public bool ShowEndpointText { get; init; } = true;
    public static TrackLineStyle Normalize(TrackLineStyle? value,TrackLineStyle defaults)
    {
        value??=defaults;
        static double Number(double n,double min,double max,double fallback)=>double.IsFinite(n)?Math.Clamp(n,min,max):fallback;
        return value with {Width=Number(value.Width,1,20,defaults.Width),Opacity=Number(value.Opacity,0,1,defaults.Opacity),
            OutlineWidth=Number(value.OutlineWidth,0,3,defaults.OutlineWidth),EndpointSize=Number(value.EndpointSize,4,14,defaults.EndpointSize),
            EndpointMinZoom=Math.Clamp(value.EndpointMinZoom,0,22),
            LineStyle=value.LineStyle is "solid" or "dash" or "dot" or "dashdot"?value.LineStyle:defaults.LineStyle,
            Endpoints=value.Endpoints is "none" or "both" or "start" or "end"?value.Endpoints:defaults.Endpoints};
    }
}
