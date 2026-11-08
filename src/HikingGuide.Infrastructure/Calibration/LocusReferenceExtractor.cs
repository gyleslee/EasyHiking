namespace HikingGuide.Infrastructure.Calibration;

public sealed record LocusReferenceSegment(string Sheet, int RowNumber, string Position,
    IReadOnlyDictionary<string, CachedReferenceCell> Fields, double? ResidualMeters,
    bool HasCompleteDistanceTargets, bool IsCompositeSuspect);
public sealed record LocusReferenceAudit(IReadOnlyList<LocusReferenceSegment> Segments,
    IReadOnlyList<string> Warnings);

public static class LocusReferenceExtractor
{
    public static readonly IReadOnlyList<string> RequiredFields = ["位置", "分段距离KM",
        "分段爬升距离KM", "分段下降距离KM", "平路距离KM", "分段爬升M", "分段下降M", "海拔M"];

    public static LocusReferenceAudit Read(string path)
    {
        var rows = new List<LocusReferenceSegment>();
        var warnings = new List<string>();
        foreach (var sheet in CachedWorkbookReader.Read(path))
        {
            var header = sheet.Rows.FirstOrDefault(row => row.Cells.Any(c => Normalize(c.Text) == "分段距离KM")
                && row.Cells.Any(c => Normalize(c.Text) == "位置"));
            if (header is null) continue;
            var columns = header.Cells.Where(c => RequiredFields.Contains(Normalize(c.Text)))
                .ToDictionary(c => Normalize(c.Text), c => Column(c.CellAddress));
            var missing = RequiredFields.Where(f => !columns.ContainsKey(f)).ToArray();
            if (missing.Length > 0) warnings.Add($"工作表“{sheet.Name}”缺少字段：{string.Join('、', missing)}。");
            foreach (var row in sheet.Rows.Where(r => r.RowNumber > header.RowNumber))
            {
                var indexed = row.Cells.ToDictionary(c => Column(c.CellAddress));
                var fields = columns.Where(pair => indexed.ContainsKey(pair.Value))
                    .ToDictionary(pair => pair.Key, pair => indexed[pair.Value]);
                var name = fields.GetValueOrDefault("位置")?.Text.Trim();
                if (string.IsNullOrWhiteSpace(name) || fields.GetValueOrDefault("分段距离KM")?.ReferenceValue is not double distance)
                    continue;
                var up = fields.GetValueOrDefault("分段爬升距离KM")?.ReferenceValue;
                var down = fields.GetValueOrDefault("分段下降距离KM")?.ReferenceValue;
                var flat = fields.GetValueOrDefault("平路距离KM")?.ReferenceValue;
                var complete = up.HasValue && down.HasValue && flat.HasValue;
                // 特殊组合只标记审计，不能据此推断区间或直接进入拟合。
                var composite = name == "向巴措" && fields.Values.Any(c => c.FormulaText is not null);
                rows.Add(new(sheet.Name, row.RowNumber, name, fields,
                    complete ? (distance - up!.Value - down!.Value - flat!.Value) * 1000 : null,
                    complete, composite));
                foreach (var pair in fields.Where(p => p.Value.Error is not null))
                    warnings.Add($"{sheet.Name}!{pair.Value.CellAddress}（{pair.Key}）：{pair.Value.Error}。");
            }
        }
        if (rows.Count == 0) warnings.Add("没有找到可用的参考分段，请核对工作表及缓存值。");
        return new(rows, warnings);
    }

    private static string Column(string address) => new(address.TakeWhile(char.IsLetter).ToArray());
    private static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)))
        .Replace("（标注点）", "").Replace("(标注点)", "");
}
