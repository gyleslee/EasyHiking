using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

public sealed record RasterLayerSlot(string Slot, MapSourceDefinition? Source, IReadOnlyList<string> Templates, bool Visible, double Opacity);
public static class MapLayerStackService
{
    public static bool IsAvailable(MapSourceDefinition source, MapSourceUsage usage) => source.Enabled && (source.EffectiveUsage & usage) != 0 && new MapSourceTemplateService().Validate(source).IsValid;
    public static GlobalMapLayerStackSettings Normalize(GlobalMapLayerStackSettings? input, IReadOnlyList<MapSourceDefinition> sources)
    {
        var s = input ?? new();
        var valid = sources.Where(v => IsAvailable(v,MapSourceUsage.Base)).ToArray();
        var b = valid.FirstOrDefault(v => v.Id == s.BaseSourceId) ?? valid.FirstOrDefault(v => v.Id == GlobalMapLayerStackSettings.GermanyId) ?? valid.FirstOrDefault();
        var used = new HashSet<Guid> { b?.Id ?? GlobalMapLayerStackSettings.GermanyId };
        Guid? Overlay(Guid? id) => id is Guid value && sources.Any(v => v.Id == value && IsAvailable(v,MapSourceUsage.Overlay)) && used.Add(value) ? value : null;
        var o1=Overlay(s.Overlay1SourceId); var o2=Overlay(s.Overlay2SourceId);
        static double Opacity(double value,double fallback) => double.IsFinite(value) ? Math.Clamp(value,0,1) : fallback;
        return s with { BaseSourceId=b?.Id ?? GlobalMapLayerStackSettings.GermanyId, BaseOpacity=Opacity(s.BaseOpacity,1), Overlay1SourceId=o1,Overlay1Visible=o1.HasValue&&s.Overlay1Visible,Overlay1Opacity=Opacity(s.Overlay1Opacity,.6), Overlay2SourceId=o2,Overlay2Visible=o2.HasValue&&s.Overlay2Visible,Overlay2Opacity=Opacity(s.Overlay2Opacity,.6) };
    }
    public static string? Validate(GlobalMapLayerStackSettings s, IReadOnlyList<MapSourceDefinition> sources)
    {
        var ids = new Guid?[] {s.BaseSourceId,s.Overlay1SourceId,s.Overlay2SourceId}.OfType<Guid>().ToArray();
        if(ids.Distinct().Count()!=ids.Length) return "该图源已经在当前地图图层中使用。";
        if(!sources.Any(v=>v.Id==s.BaseSourceId&&IsAvailable(v,MapSourceUsage.Base))) return "必须选择一个有效底图。";
        if(new[]{s.Overlay1SourceId,s.Overlay2SourceId}.OfType<Guid>().Any(id=>!sources.Any(v=>v.Id==id&&IsAvailable(v,MapSourceUsage.Overlay)))) return "叠加层图源不可用。";
        if(new[]{s.BaseOpacity,s.Overlay1Opacity,s.Overlay2Opacity}.Any(v=>!double.IsFinite(v)||v<0||v>1)) return "透明度必须在0%～100%之间。";
        return null;
    }
    public static IReadOnlyList<RasterLayerSlot> Build(GlobalMapLayerStackSettings s, IReadOnlyList<MapSourceDefinition> sources)
    {
        RasterLayerSlot Slot(string slot,Guid? id,bool visible,double opacity) { var source=sources.FirstOrDefault(v=>v.Id==id); return new(slot,source,source is null?[]:MapTileTemplateParser.Parse(source.UrlTemplate).NormalizedTemplates,source is not null&&visible,opacity); }
        return [Slot("base",s.BaseSourceId,true,s.BaseOpacity),Slot("overlay1",s.Overlay1SourceId,s.Overlay1Visible,s.Overlay1Opacity),Slot("overlay2",s.Overlay2SourceId,s.Overlay2Visible,s.Overlay2Opacity)];
    }
}
