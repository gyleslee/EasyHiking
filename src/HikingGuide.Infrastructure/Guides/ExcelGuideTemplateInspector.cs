using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HikingGuide.Infrastructure.Guides;

public sealed record ExcelGuideTemplateCell(string Address, string Text, int StyleIndex, string? Formula);
public sealed record ExcelGuideTemplateColumn(string Key, string Header, int ColumnIndex,
    int HeaderStyleIndex, int BodyStyleIndex, double Width);
public sealed record ExcelGuideTemplateMapping(string TemplateSha256, IReadOnlyList<string> SheetNames,
    string SheetEntryPath, int HeaderStartRow, int HeaderEndRow, int BodyStartRow, int SampleBodyEndRow,
    int NotesStartRow, IReadOnlyList<ExcelGuideTemplateColumn> Columns, IReadOnlyList<string> MergedRanges,
    IReadOnlyList<ExcelGuideTemplateCell> TopCells);

/// <summary>只读解析用户真实模板，提取语义和风格原型；现有公式仅作为元数据，不参与业务计算。</summary>
public static class ExcelGuideTemplateInspector
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly IReadOnlyDictionary<string, string> HeaderKeys = new Dictionary<string, string>
    {
        ["编号"] = "Index", ["位置（攻略节点）"] = "GuideNodeName", ["海拔m"] = "Elevation",
        ["参考方案"] = "ReferencePlan", ["计划抵达时间"] = "PlannedArrivalPrototype", ["实际抵达时间"] = "ActualArrival",
        ["计划用时"] = "PlannedDuration", ["均速分段用时h"] = "AverageSpeedDuration", ["奈斯密斯分段用时h"] = "NaismithDuration",
        ["距起点km"] = "DistanceFromStart", ["分段距离km"] = "SegmentDistance", ["累计爬升m"] = "CumulativeAscent",
        ["分段爬升m"] = "SegmentAscent", ["分段爬升距离km"] = "UphillDistance", ["累计下降m"] = "CumulativeDescent",
        ["分段下降m"] = "SegmentDescent", ["分段下降距离km"] = "DownhillDistance", ["平路距离km"] = "FlatDistance",
        ["分段路况"] = "RoadCondition", ["地点情况"] = "LocationConditionGroup"
    };

    public static ExcelGuideTemplateMapping Read(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = Convert.ToHexString(SHA256.HashData(stream)); stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        XDocument Xml(string entry)
        {
            var item = archive.GetEntry(entry) ?? throw new InvalidDataException("攻略模板缺少必要文件：" + entry);
            if (item.Length > 32 * 1024 * 1024) throw new InvalidDataException("攻略模板文件过大。");
            using var input = item.Open();
            using var reader = System.Xml.XmlReader.Create(input, new System.Xml.XmlReaderSettings
            { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
            return XDocument.Load(reader);
        }
        var workbook = Xml("xl/workbook.xml");
        var sheets = workbook.Root!.Element(Ns + "sheets")!.Elements(Ns + "sheet").ToArray();
        var names = sheets.Select(s => (string)s.Attribute("name")!).ToArray();
        var guideSheet = sheets.SingleOrDefault(s => (string?)s.Attribute("name") == "攻略数据") ?? throw new InvalidDataException("模板中没有正式的“攻略数据”工作表。");
        var id = (string?)guideSheet.Attribute(Rel + "id");
        var relation = Xml("xl/_rels/workbook.xml.rels").Root!.Elements().Single(r => (string?)r.Attribute("Id") == id);
        if ((string?)relation.Attribute("TargetMode") == "External") throw new InvalidDataException("正式攻略模板不能使用外部工作表。");
        var path = new Uri(new Uri("https://template.local/xl/workbook.xml"), (string)relation.Attribute("Target")!).AbsolutePath.TrimStart('/');
        if (!path.StartsWith("xl/", StringComparison.Ordinal)) throw new InvalidDataException("攻略模板工作表路径无效。");
        var worksheet = Xml(path);
        var strings = archive.GetEntry("xl/sharedStrings.xml") is null ? [] : Xml("xl/sharedStrings.xml").Root!.Elements(Ns + "si")
            .Select(si => string.Concat(si.Descendants(Ns + "t").Select(t => t.Value))).ToArray();
        ExcelGuideTemplateCell Cell(XElement c)
        {
            var text = (string?)c.Element(Ns + "v") ?? "";
            if ((string?)c.Attribute("t") == "s") text = strings[int.Parse(text, System.Globalization.CultureInfo.InvariantCulture)];
            else if ((string?)c.Attribute("t") == "inlineStr") text = string.Concat(c.Descendants(Ns + "t").Select(t => t.Value));
            return new((string)c.Attribute("r")!, text, (int?)c.Attribute("s") ?? 0, (string?)c.Element(Ns + "f"));
        }
        var rows = worksheet.Root!.Element(Ns + "sheetData")!.Elements(Ns + "row").ToArray();
        var cells = rows.SelectMany(row => row.Elements(Ns + "c")).Select(Cell).ToArray();
        var indexHeader = cells.SingleOrDefault(c => Normalize(c.Text) == "编号") ?? throw new InvalidDataException("攻略模板缺少编号表头。");
        var headerStart = RowIndex(indexHeader.Address); var headerEnd = headerStart + 1;
        var notes = cells.Where(c => c.Text.Trim().StartsWith("备注", StringComparison.Ordinal)).OrderBy(c => RowIndex(c.Address)).FirstOrDefault()
            ?? throw new InvalidDataException("攻略模板缺少备注区。");
        var noteRow = RowIndex(notes.Address);
        var bodyStart = headerEnd + 1;
        var bodyEnd = rows.Where(r => (int)r.Attribute("r")! >= bodyStart && (int)r.Attribute("r")! < noteRow)
            .Where(r => r.Elements(Ns + "c").Any(c => (string?)c.Element(Ns + "v") is not null || c.Element(Ns + "is") is not null))
            .Select(r => (int)r.Attribute("r")!).DefaultIfEmpty(bodyStart).Max();
        var prototypes = new List<ExcelGuideTemplateColumn>();
        var columnStyles = worksheet.Root.Element(Ns + "cols")?.Elements(Ns + "col").ToArray() ?? [];
        foreach (var header in cells.Where(c => RowIndex(c.Address) == headerStart && c.Text.Length > 0))
        {
            var normalized = Normalize(header.Text); var column = ColumnIndex(header.Address);
            var key = HeaderKeys.GetValueOrDefault(normalized) ?? (normalized.StartsWith("行进方案", StringComparison.Ordinal) ? "TravelPlanPrototype:" + normalized : null);
            if (key is null) continue;
            var widthElement = columnStyles.FirstOrDefault(c => (int)c.Attribute("min")! <= column && (int)c.Attribute("max")! >= column);
            var body = cells.FirstOrDefault(c => ColumnIndex(c.Address) == column && RowIndex(c.Address) == Math.Min(bodyStart + 1, bodyEnd));
            prototypes.Add(new(key, header.Text, column, header.StyleIndex, body?.StyleIndex ?? header.StyleIndex, (double?)widthElement?.Attribute("width") ?? 8.43));
        }
        foreach (var required in HeaderKeys.Values)
            if (!prototypes.Any(c => c.Key == required)) throw new InvalidDataException("攻略模板缺少语义字段：" + required);
        if (!prototypes.Any(c => c.Key.StartsWith("TravelPlanPrototype:", StringComparison.Ordinal))) throw new InvalidDataException("攻略模板缺少行进方案样式原型。");
        return new(hash, names, path, headerStart, headerEnd, bodyStart, bodyEnd, noteRow, prototypes,
            worksheet.Root.Element(Ns + "mergeCells")?.Elements(Ns + "mergeCell").Select(c => (string)c.Attribute("ref")!).ToArray() ?? [],
            cells.Where(c => RowIndex(c.Address) < headerStart).ToArray());
    }

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", "");
    private static int RowIndex(string address) => int.Parse(Regex.Match(address, @"\d+$").Value, System.Globalization.CultureInfo.InvariantCulture);
    public static int ColumnIndex(string address)
    {
        var result = 0;
        foreach (var letter in address.TakeWhile(char.IsLetter)) result = checked(result * 26 + char.ToUpperInvariant(letter) - 'A' + 1);
        return result;
    }
}
