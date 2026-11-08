using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace HikingGuide.Infrastructure.Calibration;

public sealed record CachedReferenceCell(string CellAddress, string Text,
    double? ReferenceValue, string? FormulaText, string NumberFormat, string? Error);
public sealed record CachedReferenceRow(int RowNumber, IReadOnlyList<CachedReferenceCell> Cells);
public sealed record CachedReferenceSheet(string Name, IReadOnlyList<CachedReferenceRow> Rows);

/// <summary>只读 OOXML 缓存，不执行公式，不写回工作簿；保留审计所需的原始公式和格式。</summary>
public static class CachedWorkbookReader
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static IReadOnlyList<CachedReferenceSheet> Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var strings = Load(archive, "xl/sharedStrings.xml", optional: true)?.Root?
            .Elements(Ns + "si").Select(ReadString).ToArray() ?? [];
        var styles = Load(archive, "xl/styles.xml", optional: true);
        var formats = styles?.Root?.Element(Ns + "numFmts")?.Elements(Ns + "numFmt")
            .ToDictionary(e => (int)e.Attribute("numFmtId")!, e => (string)e.Attribute("formatCode")!) ?? [];
        var styleFormats = styles?.Root?.Element(Ns + "cellXfs")?.Elements(Ns + "xf")
            .Select(e => (int?)e.Attribute("numFmtId") ?? 0).ToArray() ?? [0];
        var relationships = Load(archive, "xl/_rels/workbook.xml.rels")!.Root!.Elements()
            .ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);
        var sheets = Load(archive, "xl/workbook.xml")!.Root!.Element(Ns + "sheets")!.Elements(Ns + "sheet");
        var result = new List<CachedReferenceSheet>();
        foreach (var sheet in sheets)
        {
            var target = relationships[(string)sheet.Attribute(Rel + "id")!];
            var uri = new Uri(new Uri("https://ooxml.invalid/xl/workbook.xml"), target);
            var entryPath = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            var data = Load(archive, entryPath)!.Root!.Element(Ns + "sheetData");
            var sharedFormulas = data?.Descendants(Ns + "c")
                .Where(c => c.Element(Ns + "f") is { Value.Length: > 0 } f && (string?)f.Attribute("t") == "shared")
                .ToDictionary(c => (string)c.Element(Ns + "f")!.Attribute("si")!,
                    c => ((string)c.Attribute("r")!, c.Element(Ns + "f")!.Value)) ?? [];
            var rows = new List<CachedReferenceRow>();
            foreach (var row in data?.Elements(Ns + "row") ?? [])
            {
                var cells = new List<CachedReferenceCell>();
                foreach (var cell in row.Elements(Ns + "c"))
                {
                    var type = (string?)cell.Attribute("t");
                    var cached = (string?)cell.Element(Ns + "v") ?? "";
                    var text = type switch
                    {
                        "s" => int.TryParse(cached, out var index) && index >= 0 && index < strings.Length
                            ? strings[index] : throw new InvalidDataException("Excel共享字符串索引无效。"),
                        "inlineStr" => ReadString(cell.Element(Ns + "is")!),
                        _ => cached
                    };
                    var formula = cell.Element(Ns + "f");
                    var formulaText = formula?.Value;
                    if (formula is not null && (string?)formula.Attribute("t") == "shared" && formula.Value.Length == 0)
                    {
                        if (!sharedFormulas.TryGetValue((string?)formula.Attribute("si") ?? "", out var master))
                            throw new InvalidDataException("Excel共享公式缺少主公式。");
                        formulaText = TranslateFormula(master.Item2, master.Item1, (string)cell.Attribute("r")!);
                    }
                    var style = (int?)cell.Attribute("s") ?? 0;
                    if (style < 0 || style >= styleFormats.Length)
                        throw new InvalidDataException("Excel单元格样式索引无效。");
                    var formatId = styleFormats[style];
                    var format = formats.GetValueOrDefault(formatId) ?? BuiltInFormat(formatId);
                    double? number = type is null or "n" && double.TryParse(cached,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                        ? value : null;
                    var error = type == "e" ? text : formula is not null && cached.Length == 0
                        ? "公式没有缓存结果" : null;
                    cells.Add(new((string)cell.Attribute("r")!, text, number,
                        formula is null ? null : "=" + formulaText, format, error));
                }
                rows.Add(new((int)row.Attribute("r")!, cells));
            }
            result.Add(new((string)sheet.Attribute("name")!, rows));
        }
        return result;
    }

    private static string TranslateFormula(string formula, string origin, string target)
    {
        var (originColumn, originRow) = Address(origin);
        var (targetColumn, targetRow) = Address(target);
        // 引号中的字面量不能被当成单元格引用平移。
        var parts = Regex.Split(formula, "(\"(?:[^\"]|\"\")*\")");
        for (var i = 0; i < parts.Length; i += 2)
            parts[i] = Regex.Replace(parts[i], @"(?<![A-Za-z0-9_])(?<column>\$?[A-Z]{1,3})(?<row>\$?[1-9][0-9]*)(?![A-Za-z0-9_(])", m =>
            {
                var column = m.Groups["column"].Value;
                var row = m.Groups["row"].Value;
                var (number, index) = Address(column.TrimStart('$') + row.TrimStart('$'));
                if (!column.StartsWith('$')) number += targetColumn - originColumn;
                if (!row.StartsWith('$')) index += targetRow - originRow;
                if (number is < 1 or > 16384 || index is < 1 or > 1048576) return "#REF!";
                var letters = "";
                while (number > 0) { number--; letters = (char)('A' + number % 26) + letters; number /= 26; }
                return (column.StartsWith('$') ? "$" : "") + letters + (row.StartsWith('$') ? "$" : "") + index;
            });
        return string.Concat(parts);
    }

    private static (int Column, int Row) Address(string address)
    {
        var letters = new string(address.TakeWhile(char.IsLetter).ToArray());
        var column = letters.Aggregate(0, (sum, c) => sum * 26 + c - 'A' + 1);
        return (column, int.Parse(address[letters.Length..], CultureInfo.InvariantCulture));
    }

    private static string ReadString(XElement element) => string.Concat(element
        .Descendants(Ns + "t").Where(e => !e.Ancestors(Ns + "rPh").Any()).Select(e => e.Value));

    private static XDocument? Load(ZipArchive archive, string entryPath, bool optional = false)
    {
        var entry = archive.GetEntry(entryPath);
        if (entry is null)
        {
            if (optional) return null;
            throw new InvalidDataException($"Excel缺少必需内容：{entryPath}");
        }
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024
        });
        return XDocument.Load(reader);
    }

    private static string BuiltInFormat(int id) => id switch
    {
        0 => "General", 1 => "0", 2 => "0.00", 3 => "#,##0", 4 => "#,##0.00",
        9 => "0%", 10 => "0.00%", 11 => "0.00E+00", 14 => "mm-dd-yy",
        49 => "@", _ => $"Builtin:{id}"
    };
}
