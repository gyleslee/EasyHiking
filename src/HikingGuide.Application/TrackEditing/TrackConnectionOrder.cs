using System.Globalization;

namespace HikingGuide.Application.TrackEditing;

/// <summary>连接顺序必须是1至所选项数量的一个完整排列，与GUI无关。</summary>
public static class TrackConnectionOrder
{
    public const string ErrorMessage = "连接顺序必须从1开始连续填写且不能重复，例如1、2、3。请检查是否有空白、非整数、重复或跳号。";

    public static IReadOnlyList<int> OrderedIndices(IReadOnlyList<string> values)
    {
        var parsed=new List<(int Order,int Index)>();
        for(var i=0;i<values.Count;i++)
        {
            var text=values[i].Trim();
            if(!int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var order)||
                order<1||order>values.Count||order.ToString(CultureInfo.InvariantCulture)!=text)
                throw new ArgumentException(ErrorMessage);
            parsed.Add((order,i));
        }
        if(parsed.Select(p=>p.Order).Distinct().Count()!=values.Count)throw new ArgumentException(ErrorMessage);
        return parsed.OrderBy(p=>p.Order).Select(p=>p.Index).ToArray();
    }
}
