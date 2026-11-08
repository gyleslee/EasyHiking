using System.Globalization;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.TrackEditing;

public sealed record SplitSectionSequenceChange(string? Before,string After);

/// <summary>编号以轨迹为范围；现有Metadata保存高水位，删除分段不会使编号回退。</summary>
public static class TrackSplitNaming
{
    public const string LastNumberKey="hg:split-section-last-number";
    private const string NumberKey="hg:split-section-number";
    private const string OwnerKey="hg:split-section-number-track-id";

    public sealed record Allocation(long First,long Second,string Prefix,SplitSectionSequenceChange Sequence)
    {
        public string Name(long number)=>$"{Prefix}（{number.ToString(CultureInfo.InvariantCulture)}）";
    }

    public static Allocation Prepare(Track track,Guid sectionId)
    {
        var prefix=string.IsNullOrWhiteSpace(track.Name)?"未命名轨迹":track.Name;
        track.Metadata.TryGetValue(LastNumberKey,out var before);
        var last=0L;
        if(before is not null&&(!long.TryParse(before,NumberStyles.None,CultureInfo.InvariantCulture,out last)||last<0))
            throw new InvalidOperationException("拆分分段的编号记录无效，请检查项目或从备份恢复。项目未修改。");
        var numbers=track.GetSplitSections().ToDictionary(s=>s.Id,s=>ReadNumber(track,s,prefix));
        last=Math.Max(last,numbers.Values.Max()??0);
        long Next(){if(last==long.MaxValue)throw new InvalidOperationException("拆分分段编号已达到上限，项目未修改。");return ++last;}
        var known=numbers.GetValueOrDefault(sectionId);
        var first=known is >0&&!numbers.Any(p=>p.Key!=sectionId&&p.Value==known)?known.Value:Next();
        var second=Next();
        return new(first,second,prefix,new(before,last.ToString(CultureInfo.InvariantCulture)));
    }

    public static void NamePart(Track track,TrackSegment part,Allocation allocation,bool second)
    {
        var number=second?allocation.Second:allocation.First;
        part.SplitSectionName=allocation.Name(number);
        part.OriginalAttributes[NumberKey]=number.ToString(CultureInfo.InvariantCulture);
        part.OriginalAttributes[OwnerKey]=track.Id.ToString("D");
    }

    private static long? ReadNumber(Track track,SplitSection section,string prefix)
    {
        var attributes=section.GeometryParts[0].OriginalAttributes;
        if(attributes.GetValueOrDefault(OwnerKey)==track.Id.ToString("D")&&
            long.TryParse(attributes.GetValueOrDefault(NumberKey),NumberStyles.None,CultureInfo.InvariantCulture,out var saved)&&saved>0)return saved;
        var start=prefix+"（";
        if(section.Name.StartsWith(start,StringComparison.Ordinal)&&section.Name.EndsWith("）",StringComparison.Ordinal)&&
            long.TryParse(section.Name.Substring(start.Length,section.Name.Length-start.Length-1),NumberStyles.None,CultureInfo.InvariantCulture,out var number)&&number>0)return number;
        return null;
    }
}
