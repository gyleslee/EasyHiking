using HikingGuide.Core.Guides;

namespace HikingGuide.Application.Guides;

/// <summary>界面提示与全局列身份；不改变统计定义、项目或Excel快照。</summary>
public static class GuideTablePresentation
{
    public static string Number(double value, int decimals) => GuideDisplayFormats.Number(value, decimals);

    public static string CellNumber(string key, double value) => Number(value, GuideDisplayFormats.Decimals(key));

    public static string BoundaryLabel(string fullText) => string.Join('\n', fullText.Split('\n')
        .Where(line => line.StartsWith('D') && (line.EndsWith("起点", StringComparison.Ordinal) || line.EndsWith("终点", StringComparison.Ordinal))));

    public static string BoundaryTooltip(string fullText) => string.IsNullOrEmpty(fullText)
        ? "点击设置/取消每日起点；首节点固定，末节点只作终点。" : fullText;

    public static string JoinStatusMessages(IEnumerable<string> messages) => string.Join("；", messages
        .Select(text => text.Trim().TrimEnd('。', '；', ';', '，', ',', '.', '·').TrimEnd())
        .Where(text => text.Length > 0).Distinct(StringComparer.Ordinal));

    // 项目内的Plan/地点GUID不能作为全局宽度身份。
    // 方案按可见编号对应，地点按名称对应，其余列按字段对应。
    public static string ColumnWidthKey(GuideOutputColumn column) =>
        column.Location ? "Guide:Location:" + column.Header.Trim() :
        column.Key.StartsWith("Plan:", StringComparison.Ordinal) ? "Guide:Plan:" + column.Header :
        column.Key.StartsWith("Arrival:", StringComparison.Ordinal) ? "Guide:Arrival:" + column.Header :
        "Guide:" + column.Key;
}
