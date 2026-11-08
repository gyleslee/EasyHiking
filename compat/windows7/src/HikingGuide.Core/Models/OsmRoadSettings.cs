using System.Text.Json;

namespace HikingGuide.Core.Models;

/// <summary>软件级只读路网显示设置，不进入项目文件，也不参与路线规划。</summary>
public sealed class OsmRoadSettings
{
    public OsmRoadSettings():this(OsmRoadDefaults.DisplayRulesVersion){}
    [System.Text.Json.Serialization.JsonConstructor]
    public OsmRoadSettings(int displayRulesVersion){DisplayRulesVersion=displayRulesVersion;}
    public int DisplayRulesVersion {get;set;}
    public bool Enabled {get;set;}
    public double Opacity {get;set;}=1;
    public int CacheCapacityMb {get;set;}=2048;
    public int MinimumZoom {get;set;}=10;
    public List<OsmRoadStyle> Styles {get;set;}=OsmRoadDefaults.CreateStyles();
    public Dictionary<string,OsmRoadZoomRange> DefaultZoomLevels {get;set;}=[];
    public bool IsValid=>double.IsFinite(Opacity)&&Opacity is >=0 and <=1&&CacheCapacityMb is >=0 and <=1048576&&MinimumZoom is >=8 and <=14&&
        Styles is {Count:>0 and <=1000}&&Styles.All(s=>s is not null&&s.IsValid)&&Styles.Select(s=>s.Id).Distinct().Count()==Styles.Count;
    public OsmRoadSettings DeepCopy()=>JsonSerializer.Deserialize<OsmRoadSettings>(JsonSerializer.Serialize(this))!;
    public void RememberDisplayLevels()=>DefaultZoomLevels=Styles.ToDictionary(s=>s.Id,s=>new OsmRoadZoomRange(s.MinZoom,s.MaxZoom));
    public void RestoreDefaultStyles()
    {
        Styles=OsmRoadDefaults.CreateStyles();
        foreach(var style in Styles)
            if(DefaultZoomLevels is not null&&DefaultZoomLevels.TryGetValue(style.Id,out var range)&&range is {IsValid:true})
            {style.MinZoom=range.MinZoom;style.MaxZoom=range.MaxZoom;}
    }
    public void UpgradeDisplayRules()
    {
        if(DisplayRulesVersion<OsmRoadDefaults.DisplayRulesVersion)
        {Styles=OsmRoadDefaults.CreateStyles();DefaultZoomLevels=[];DisplayRulesVersion=OsmRoadDefaults.DisplayRulesVersion;RememberDisplayLevels();}
        else
        {
            var saved=Styles.GroupBy(s=>s.Id).ToDictionary(g=>g.Key,g=>g.First());
            Styles=OsmRoadDefaults.CreateStyles();
            foreach(var style in Styles)if(saved.TryGetValue(style.Id,out var old)&&old.MinZoom is >=0 and <=22&&old.MaxZoom>=old.MinZoom&&old.MaxZoom<=22)
            {style.MinZoom=old.MinZoom;style.MaxZoom=old.MaxZoom;}
        }
    }
}
public sealed record OsmRoadZoomRange(int MinZoom,int MaxZoom)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid=>MinZoom is >=0 and <=22&&MaxZoom>=MinZoom&&MaxZoom<=22;
}
public sealed class OsmRoadStyle
{
    public string Id {get;set;}="";
    public string Name {get;set;}="";
    public string Group {get;set;}="普通道路";
    public bool Enabled {get;set;}=true;
    public List<OsmRoadCondition> Conditions {get;set;}=[];
    public int MinZoom {get;set;}
    public int MaxZoom {get;set;}=22;
    public string Color {get;set;}="#707070";
    public double Width {get;set;}=1;
    public double Alpha {get;set;}=1;
    public string DashArray {get;set;}="";
    public string LineCap {get;set;}="butt";
    public string ScaleMode {get;set;}="stroke";
    public string CasingColor {get;set;}="#FFFFFF";
    public bool CasingEnabled {get;set;}
    /// <summary>每侧描边的基础宽度；与主题基础宽度一致，放大时按比例变化。</summary>
    public double CasingWidth {get;set;}
    public double CasingAlpha {get;set;}=1;
    public string CasingDashArray {get;set;}="";
    public string CasingCap {get;set;}="butt";
    public string CasingScaleMode {get;set;}="stroke";
    public int Order {get;set;}
    public int CasingOrder {get;set;}
    public string Description {get;set;}="";
    public bool IsValid=>Id is {Length:>0 and <=100}&&Name is {Length:<=200}&&Conditions is {Count:>0 and <=20}&&
        Conditions.All(c=>c is not null&&c.Key is {Length:>0 and <=100}&&c.Values is {Length:>0 and <=1000})&&
        MinZoom is >=0 and <=22&&MaxZoom>=MinZoom&&MaxZoom<=22&&ValidColor(Color)&&ValidColor(CasingColor)&&
        double.IsFinite(Width)&&Width is >=0 and <=30&&double.IsFinite(CasingWidth)&&CasingWidth is >=0 and <=30&&
        double.IsFinite(Alpha)&&Alpha is >=0 and <=1&&double.IsFinite(CasingAlpha)&&CasingAlpha is >=0 and <=1&&
        ValidDash(DashArray)&&ValidDash(CasingDashArray)&&(LineCap is "butt" or "round" or "square")&&(CasingCap is "butt" or "round" or "square")&&
        (ScaleMode is "stroke" or "all" or "none")&&(CasingScaleMode is "stroke" or "all" or "none");
    public static bool ValidColor(string? value)=>value is {Length:7}&&value[0]=='#'&&value.Skip(1).All(Uri.IsHexDigit);
    public static bool ValidDash(string? value)=>value is not null&&value.Length<=100&&(string.IsNullOrWhiteSpace(value)||
        value.Split(',').Length is >=2 and <=12&&value.Split(',').All(s=>double.TryParse(s.Trim(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var n)&&double.IsFinite(n)&&n is >=0 and <=100)&&value.Split(',').Any(s=>double.Parse(s,System.Globalization.CultureInfo.InvariantCulture)>0));
}
public sealed record OsmRoadCondition(string Key,string Values);
public static class OsmRoadDefaults
{
    public const string SourceTheme="Locus-V5-outdoor.zip";
    public const int DisplayRulesVersion=3;
    public const int RoadMaximumZoom=22;
    private static readonly Dictionary<string,string> Names=new()
    {
        ["motorway"]="高速公路",["trunk"]="快速路",["primary"]="主干道",["secondary"]="次干道",["tertiary"]="三级道路",
        ["motorway_link"]="高速公路匝道",["trunk_link"]="快速路匝道",["primary_link"]="主干道匝道",["secondary_link"]="次干道匝道",["tertiary_link"]="三级道路匝道",
        ["unclassified"]="一般道路",["residential"]="住宅道路",["living_street"]="生活街道",["road"]="未分类道路",["service"]="服务道路",
        ["track"]="机耕道",["path"]="徒步小径",["footway"]="人行步道",["platform"]="站台通道",["pedestrian"]="步行街",["steps"]="台阶",["cycleway"]="自行车道",["bridleway"]="骑马道",["construction"]="施工道路",["byway"]="乡间小路",["bus_guideway"]="导向公交道",["raceway"]="赛道"
    };
    private static readonly Lazy<string> ThemeJson=new(()=>
    {
        using var input=typeof(OsmRoadDefaults).Assembly.GetManifestResourceStream("HikingGuide.Core.Resources.OsmRoadDefaultStyles.json")??throw new InvalidOperationException("缺少OSM道路主题。");
        using var reader=new System.IO.StreamReader(input);return reader.ReadToEnd();
    });
    public static List<OsmRoadStyle> ThemeStrokes()=>JsonSerializer.Deserialize<List<OsmRoadStyle>>(ThemeJson.Value)!;
    public static List<OsmRoadStyle> PreviewStrokes(string typeId,int zoom=16)
    {
        var type=typeId.StartsWith("highway-",StringComparison.Ordinal)?typeId[8..]:typeId;
        var tags=new Dictionary<string,string>{{"highway",type}};
        return ThemeStrokes().Where(s=>s.MinZoom<=zoom&&s.MaxZoom>=zoom&&s.Conditions.All(c=>c.Key.Split('|').Any(k=>c.Values.Split('|').Any(v=>v=="~"?!tags.ContainsKey(k):v=="*"?tags.ContainsKey(k):tags.GetValueOrDefault(k)==v)))).ToList();
    }
    private static bool Applies(OsmRoadStyle stroke,string type)
        =>stroke.Conditions.Where(c=>c.Key=="highway").All(c=>c.Values=="*"||c.Values.Split('|').Contains(type))&&
          stroke.Conditions.Where(c=>c.Key.Contains('|')&&c.Key.Split('|').Contains("highway")).All(c=>c.Values=="*"||c.Values.Split('|').Contains(type));
    public static List<OsmRoadStyle> CreateStyles()
    {
        var strokes=ThemeStrokes();var result=new List<OsmRoadStyle>();
        foreach(var (type,name) in Names)
        {
            var matching=strokes.Where(s=>s.Conditions.Any(c=>c.Key.Split('|').Contains("highway")&&c.Values.Split('|').Contains(type))&&Applies(s,type)).ToArray();
            if(matching.Length==0)continue;
            var plain=matching.Where(s=>!s.Conditions.Any(c=>c.Key is "sac_scale" or "embankment"||c.Key is "tunnel" or "bridge"&&!c.Values.Split('|').Contains("~"))).ToArray();
            var ink=plain.Where(s=>!s.Color.Equals("#FFFFFF",StringComparison.OrdinalIgnoreCase)&&s.Alpha>0).ToArray();
            var baseline=ink.Length>0?ink:plain.Length>0?plain:matching;
            result.Add(new(){Id="highway-"+type,Name=name,Conditions=[new("highway",type)],MinZoom=baseline.Min(s=>s.MinZoom),MaxZoom=baseline.Max(s=>s.MaxZoom),Description="样式由Locus-V5-outdoor主题控制，自动匹配路面、桥梁、隧道及徒步难度。"});
        }
        return result;
    }
    /// <summary>将一个道路类型的用户显示范围应用到只读主题，保留内部有限层级及原绘制顺序。</summary>
    public static List<OsmRoadStyle> BuildRenderStyles(OsmRoadSettings settings)
    {
        var defaults=CreateStyles().ToDictionary(s=>s.Id);var types=settings.Styles.Where(s=>defaults.ContainsKey(s.Id)).ToArray();var result=new List<OsmRoadStyle>();var theme=ThemeStrokes();
        bool Special(string type,string key)=>theme.Any(s=>Applies(s,type)&&s.Conditions.Any(c=>c.Key==key&&!c.Values.Split('|').Contains("~")));
        foreach(var stroke in theme)
        {
            var scopes=new List<(string Type,int Min,int Max,bool BridgeFallback,bool TunnelFallback)>();
            foreach(var type in types)
            {
                var highway=type.Conditions[0].Values;if(!Applies(stroke,highway))continue;
                var original=defaults[type.Id];
                var min=stroke.MinZoom<=original.MinZoom?type.MinZoom:Math.Max(type.MinZoom,stroke.MinZoom);
                var max=stroke.MaxZoom>=original.MaxZoom?type.MaxZoom:Math.Min(type.MaxZoom,stroke.MaxZoom);
                if(min<=max)scopes.Add((highway,min,max,!Special(highway,"bridge"),!Special(highway,"tunnel")));
            }
            foreach(var group in scopes.GroupBy(s=>(s.Min,s.Max,s.BridgeFallback,s.TunnelFallback)))
            {
                var copy=JsonSerializer.Deserialize<OsmRoadStyle>(JsonSerializer.Serialize(stroke))!;
                copy.Id+="-"+group.Key.Min+"-"+group.Key.Max+"-"+group.Key.BridgeFallback+"-"+group.Key.TunnelFallback;copy.MinZoom=group.Key.Min;copy.MaxZoom=group.Key.Max;
                copy.Conditions.RemoveAll(c=>c.Values.Split('|').Contains("~")&&((group.Key.BridgeFallback&&c.Key=="bridge")||(group.Key.TunnelFallback&&c.Key=="tunnel")));
                copy.Conditions.Add(new("highway",string.Join('|',group.Select(s=>s.Type))));result.Add(copy);
            }
        }
        return result;
    }
}
