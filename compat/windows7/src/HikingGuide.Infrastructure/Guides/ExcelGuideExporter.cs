using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using HikingGuide.Core.Guides;

namespace HikingGuide.Infrastructure.Guides;

/// <summary>克隆真实模板资源，按语义布局写入最终数值；安全临时文件验证后才替换目标。</summary>
public sealed class ExcelGuideExporter
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public async Task ExportAsync(string template, string destination, GuideExportSnapshot snapshot,
        bool overwrite = false, CancellationToken cancellationToken = default)
    {
        if (Path.GetFullPath(template).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) throw new IOException("不能覆盖攻略模板原文件。");
        if (snapshot.Rows.Count < 2) throw new InvalidOperationException("至少需要两个攻略节点。");
        var full = Path.GetFullPath(destination);
        if (File.Exists(full) && !overwrite) throw new IOException("输出文件已存在，请确认覆盖。");
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await Task.Run(() => Write(template, temporary, snapshot, cancellationToken), cancellationToken);
            await Task.Run(() => Verify(temporary, snapshot.Rows.Count), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(full))
            {
                if (!overwrite) throw new IOException("输出文件已存在，请确认覆盖。");
                File.Replace(temporary, full, null);
            }
            else File.Move(temporary, full);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IReadOnlyList<string> SplitCellText(string value)
    {
        var result = new List<string>();
        for (var offset = 0; offset < value.Length;)
        {
            var length = Math.Min(32767, value.Length - offset);
            if (offset + length < value.Length && char.IsHighSurrogate(value[offset + length - 1])) length--;
            result.Add(value.Substring(offset, length)); offset += length;
        }
        return result.Count == 0 ? [""] : result;
    }

    private static void Write(string template, string output, GuideExportSnapshot snapshot, CancellationToken token)
    {
        var mapping = ExcelGuideTemplateInspector.Read(template);
        using var source = ZipFile.OpenRead(template);
        XDocument Read(string path) { using var input = source.GetEntry(path)!.Open(); return XDocument.Load(input); }
        var worksheet = Read(mapping.SheetEntryPath); var root = worksheet.Root!;
        var oldData = root.Element(Ns + "sheetData")!;
        var samples = oldData.Elements(Ns + "row").ToDictionary(r => (int)r.Attribute("r")!);
        var roadParts = snapshot.Rows.Select(r => SplitCellText((string?)r.Values.GetValueOrDefault("RoadCondition") ?? "")).ToArray();
        var continuationCount = roadParts.Max(p => p.Count) - 1;
        var columns = snapshot.Columns.ToList(); var roadIndex = columns.FindIndex(c => c.Key == "RoadCondition");
        for (var i = 1; i <= continuationCount; i++) columns.Insert(roadIndex + i, new("RoadContinuation:" + i, $"分段路况（续{i}）", "RoadCondition"));
        var prototypes = mapping.Columns.ToDictionary(c => c.Key);
        ExcelGuideTemplateColumn Prototype(GuideOutputColumn c) => c.TemplateKey == "TravelPlanPrototype"
            ? mapping.Columns.First(p => p.Key.StartsWith("TravelPlanPrototype:", StringComparison.Ordinal)) : prototypes[c.TemplateKey];
        var styles = Read("xl/styles.xml"); var xfs = styles.Root!.Element(Ns + "cellXfs")!;
        var timeStyle = new XElement(xfs.Elements().ElementAt(prototypes["ActualArrival"].BodyStyleIndex));
        var timeStyleId = xfs.Elements().Count(); xfs.Add(timeStyle); xfs.SetAttributeValue("count", xfs.Elements().Count());
        var formats = styles.Root.Element(Ns + "numFmts");
        if (formats is null) { formats = new XElement(Ns + "numFmts", new XAttribute("count", 0)); styles.Root.AddFirst(formats); }
        var oneDecimal = formats.Elements().FirstOrDefault(f => (string?)f.Attribute("formatCode") == "0.0");
        var oneDecimalId = oneDecimal is null ? Math.Max(163, formats.Elements().Select(f => (int)f.Attribute("numFmtId")!).DefaultIfEmpty(163).Max()) + 1 : (int)oneDecimal.Attribute("numFmtId")!;
        if (oneDecimal is null) { formats.Add(new XElement(Ns + "numFmt", new XAttribute("numFmtId", oneDecimalId), new XAttribute("formatCode", "0.0"))); formats.SetAttributeValue("count", formats.Elements().Count()); }
        var timeFormat = formats.Elements().FirstOrDefault(f => (string?)f.Attribute("formatCode") == "hh:mm");
        var timeFormatId = timeFormat is null ? Math.Max(163, formats.Elements().Select(f => (int)f.Attribute("numFmtId")!).DefaultIfEmpty(163).Max()) + 1 : (int)timeFormat.Attribute("numFmtId")!;
        if (timeFormat is null) { formats.Add(new XElement(Ns + "numFmt", new XAttribute("numFmtId", timeFormatId), new XAttribute("formatCode", "hh:mm"))); formats.SetAttributeValue("count", formats.Elements().Count()); }
        timeStyle.SetAttributeValue("numFmtId", timeFormatId); timeStyle.SetAttributeValue("applyNumberFormat", 1);
        var numericStyles = new Dictionary<(int, int), int>();
        int NumberStyle(int baseStyle, int decimals)
        {
            if (numericStyles.TryGetValue((baseStyle, decimals), out var hit)) return hit;
            var style = new XElement(xfs.Elements().ElementAt(baseStyle)); style.SetAttributeValue("numFmtId", decimals == 0 ? 1 : decimals == 1 ? oneDecimalId : 2); style.SetAttributeValue("applyNumberFormat", 1);
            var index = xfs.Elements().Count(); xfs.Add(style); xfs.SetAttributeValue("count", index + 1); numericStyles[(baseStyle, decimals)] = index; return index;
        }
        var leftStyles = new Dictionary<int, int>();
        int LeftStyle(int baseStyle)
        {
            if (leftStyles.TryGetValue(baseStyle, out var hit)) return hit;
            var style = new XElement(xfs.Elements().ElementAt(baseStyle)); var alignment = style.Element(Ns + "alignment");
            if (alignment is null) { alignment = new XElement(Ns + "alignment"); style.Add(alignment); }
            alignment.SetAttributeValue("horizontal", "left"); alignment.SetAttributeValue("vertical", "center"); alignment.SetAttributeValue("wrapText", 1); style.SetAttributeValue("applyAlignment", 1);
            var index = xfs.Elements().Count(); xfs.Add(style); xfs.SetAttributeValue("count", index + 1); leftStyles[baseStyle] = index; return index;
        }
        XElement Cell(int col, int row, object? value, int style)
        {
            var c = new XElement(Ns + "c", new XAttribute("r", ColumnName(col) + row), new XAttribute("s", style));
            if (value is TimeOnly time) c.Add(new XElement(Ns + "v", time.ToTimeSpan().TotalDays.ToString("R", CultureInfo.InvariantCulture)));
            else if (value is double number) { if (!double.IsFinite(number)) throw new InvalidDataException("导出数值无效。"); c.Add(new XElement(Ns + "v", number.ToString("R", CultureInfo.InvariantCulture))); }
            else if (value is int integer) c.Add(new XElement(Ns + "v", integer));
            else if (value is string text && text.Length > 0)
            {
                if (text.Length > 32767) throw new InvalidDataException("非路况文本超过Excel单元格长度限制。");
                c.SetAttributeValue("t", "inlineStr"); c.Add(new XElement(Ns + "is", new XElement(Ns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
            }
            return c;
        }
        var data = new XElement(Ns + "sheetData"); var last = columns.Count;
        var speedStart = columns.FindIndex(c => c.Key == "RoadCondition") + 1; var titleEnd = speedStart - 1;
        var titleStyle = mapping.TopCells.First(c => c.Text == "攻略轨迹名称").StyleIndex;
        var titlePrototypeEnd = ExcelGuideTemplateInspector.ColumnIndex(mapping.MergedRanges.First(m => m.StartsWith("A1:", StringComparison.Ordinal)).Split(':')[1]);
        var speedLabel = mapping.TopCells.First(c => c.Text.StartsWith("下降、平路速度", StringComparison.Ordinal));
        var speedNumberStyle = mapping.TopCells.First(c => c.Address == ColumnName(ExcelGuideTemplateInspector.ColumnIndex(speedLabel.Address) + 1) + "1").StyleIndex;
        for (var r = 1; r < mapping.HeaderStartRow; r++)
        {
            var row = new XElement(Ns + "row", new XAttribute("r", r));
            if (samples.TryGetValue(r, out var original)) foreach (var a in original.Attributes().Where(a => a.Name != "r")) row.SetAttributeValue(a.Name, a.Value);
            for (var col = 1; col <= titleEnd; col++)
            {
                var sourceColumn = col == titleEnd ? titlePrototypeEnd : Math.Min(col, titlePrototypeEnd - 1);
                var style = mapping.TopCells.FirstOrDefault(c => c.Address == ColumnName(sourceColumn) + r)?.StyleIndex ?? titleStyle;
                row.Add(Cell(col, r, col == 1 && r == 1 ? snapshot.TrackName : null, style));
            }
            if (r == 1) { row.Add(Cell(speedStart, r, "下降、平路速度km/h", speedLabel.StyleIndex)); row.Add(Cell(speedStart + 1, r, snapshot.Settings.FlatDownSpeedKmh, speedNumberStyle)); }
            if (r == 2) { row.Add(Cell(speedStart, r, "缓坡/陡坡爬升速度km/h", speedLabel.StyleIndex)); row.Add(Cell(speedStart + 1, r, snapshot.Settings.GentleUpSpeedKmh, speedNumberStyle)); row.Add(Cell(speedStart + 2, r, snapshot.Settings.SteepUpSpeedKmh, speedNumberStyle)); }
            data.Add(row);
        }
        var merges = new List<string> { $"A1:{ColumnName(titleEnd)}2" };
        var locationStart = columns.FindIndex(c => c.Location) + 1;
        for (var r = mapping.HeaderStartRow; r <= mapping.HeaderEndRow; r++)
        {
            var row = new XElement(Ns + "row", new XAttribute("r", r));
            if (samples.TryGetValue(r, out var prototypeRow)) foreach (var a in prototypeRow.Attributes().Where(a => a.Name != "r")) row.SetAttributeValue(a.Name, a.Value);
            for (var col = 1; col <= last; col++)
            {
                var definition = columns[col - 1]; var prototype = Prototype(definition);
                row.Add(Cell(col, r, definition.Location ? r == mapping.HeaderStartRow ? col == locationStart ? "地点情况" : null : definition.Header :
                    r == mapping.HeaderStartRow ? definition.Header : null, definition.Location && r == mapping.HeaderEndRow ? 9 : prototype.HeaderStyleIndex));
                if (r == mapping.HeaderStartRow && !definition.Location) merges.Add($"{ColumnName(col)}{r}:{ColumnName(col)}{mapping.HeaderEndRow}");
            }
            data.Add(row);
        }
        if (locationStart > 0 && locationStart < last) merges.Add($"{ColumnName(locationStart)}{mapping.HeaderStartRow}:{ColumnName(last)}{mapping.HeaderStartRow}");
        for (var i = 0; i < snapshot.Rows.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var r = mapping.BodyStartRow + i;
            var row = new XElement(Ns + "row", new XAttribute("r", r));
            var sample = samples.GetValueOrDefault(i == 0 ? mapping.BodyStartRow : mapping.BodyStartRow + 1);
            if (sample is not null) foreach (var a in sample.Attributes().Where(a => a.Name != "r")) row.SetAttributeValue(a.Name, a.Value);
            // 长文本及每日边界不固定行高，让WPS/Excel可以展开换行；基础行仍沿用模板。
            if (snapshot.Rows[i].Values.Any(v => v.Value is string text && (text.Contains('\n') || text.Length > 120))) { row.Attribute("ht")?.Remove(); row.Attribute("customHeight")?.Remove(); }
            for (var col = 1; col <= last; col++)
            {
                var c = columns[col - 1]; object? value = c.Key.StartsWith("RoadContinuation:", StringComparison.Ordinal) ? roadParts[i].ElementAtOrDefault(int.Parse(c.Key.Split(':')[1], CultureInfo.InvariantCulture)) : snapshot.Rows[i].Values.GetValueOrDefault(c.Key);
                if (c.Key == "RoadCondition") value = roadParts[i][0];
                if (c.Key.StartsWith("Plan:", StringComparison.Ordinal))
                    value = string.Join('\n', snapshot.PlanTimings[Guid.Parse(c.Key[5..])].Days.Where(d => d.EndNodeId == snapshot.Rows[i].NodeId).Select(GuideDisplayFormats.Summary));
                var prototype = Prototype(c); var style = c.Location ? 9 : prototype.BodyStyleIndex;
                if (c.Key.StartsWith("Plan:", StringComparison.Ordinal)) style = LeftStyle(style);
                else if (c.Time) style = timeStyleId;
                else if (value is double) style = NumberStyle(style, GuideDisplayFormats.Decimals(c.Key));
                row.Add(Cell(col, r, value, style));
            }
            data.Add(row);
        }
        var notesStart = mapping.BodyStartRow + snapshot.Rows.Count + 1;
        if (samples.TryGetValue(mapping.NotesStartRow - 1, out var gapPrototype))
        {
            var gap = new XElement(Ns + "row", new XAttribute("r", notesStart - 1));
            foreach (var a in gapPrototype.Attributes().Where(a => a.Name != "r")) gap.SetAttributeValue(a.Name, a.Value);
            var gapStyle = (int?)gapPrototype.Elements(Ns + "c").FirstOrDefault()?.Attribute("s") ?? 0;
            for (var col = 1; col <= last; col++) gap.Add(Cell(col, notesStart - 1, null, gapStyle));
            data.Add(gap); merges.Add($"A{notesStart - 1}:{ColumnName(last)}{notesStart - 1}");
        }
        var lastNotes = samples.Keys.Max();
        var userNotes = snapshot.Columns.Where(c => c.Key.StartsWith("Plan:", StringComparison.Ordinal))
            .SelectMany(c => snapshot.PlanTimings[Guid.Parse(c.Key[5..])].Days.Where(d => !string.IsNullOrEmpty(d.Notes))
                .SelectMany(d => (c.Header + $" D{d.DayNumber}：" + d.Notes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').SelectMany(SplitCellText)))
            .ToArray();
        for (var original = mapping.NotesStartRow; original <= lastNotes; original++)
        {
            if (!samples.TryGetValue(original, out var sample)) continue;
            var rowNumber = notesStart + original - mapping.NotesStartRow + (original == mapping.NotesStartRow ? 0 : userNotes.Length);
            var row = new XElement(Ns + "row", new XAttribute("r", rowNumber));
            foreach (var a in sample.Attributes().Where(a => a.Name != "r")) row.SetAttributeValue(a.Name, a.Value);
            // 备注文本可能是sharedString，克隆单元格并仅平移行号，字符串表随模板保留。
            foreach (var cell in sample.Elements(Ns + "c"))
            {
                var clone = new XElement(cell); var col = ExcelGuideTemplateInspector.ColumnIndex((string)cell.Attribute("r")!);
                if (col > last) continue;
                clone.SetAttributeValue("r", ColumnName(col) + rowNumber); clone.Element(Ns + "f")?.Remove(); row.Add(clone);
            }
            var lastStyled = sample.Elements(Ns + "c").LastOrDefault(c => (int?)c.Attribute("s") > 0);
            var noteStyle = (int?)lastStyled?.Attribute("s") ?? 0;
            var occupied = row.Elements(Ns + "c").Select(c => ExcelGuideTemplateInspector.ColumnIndex((string)c.Attribute("r")!)).ToHashSet();
            for (var col = 1; col <= last; col++) if (!occupied.Contains(col)) row.Add(Cell(col, rowNumber, null, noteStyle));
            row.ReplaceNodes(row.Elements(Ns + "c").OrderBy(c => ExcelGuideTemplateInspector.ColumnIndex((string)c.Attribute("r")!)).ToArray());
            data.Add(row);
            if (mapping.MergedRanges.Any(m => m.StartsWith("A" + original + ":", StringComparison.Ordinal))) merges.Add($"A{rowNumber}:{ColumnName(last)}{rowNumber}");
            if (original == mapping.NotesStartRow)
            {
                // 人工备注紧跟“备注：”，模板原来的空白备注行和非空原文平移到后面，不覆盖。
                var body = samples.GetValueOrDefault(original + 1) ?? sample;
                var bodyStyle = LeftStyle((int?)body.Elements(Ns + "c").FirstOrDefault()?.Attribute("s") ?? noteStyle);
                for (var i = 0; i < userNotes.Length; i++)
                {
                    token.ThrowIfCancellationRequested(); var r = notesStart + 1 + i;
                    var noteRow = new XElement(Ns + "row", new XAttribute("r", r));
                    foreach (var a in body.Attributes().Where(a => a.Name != "r" && a.Name != "ht" && a.Name != "customHeight")) noteRow.SetAttributeValue(a.Name, a.Value);
                    for (var col = 1; col <= last; col++) noteRow.Add(Cell(col, r, col == 1 ? userNotes[i] : null, bodyStyle));
                    data.Add(noteRow); merges.Add($"A{r}:{ColumnName(last)}{r}");
                }
            }
        }
        var lastOutputRow = notesStart + lastNotes - mapping.NotesStartRow + userNotes.Length;
        oldData.ReplaceWith(data);
        root.Element(Ns + "cols")?.ReplaceWith(new XElement(Ns + "cols", columns.Select((c, i) => new XElement(Ns + "col", new XAttribute("min", i + 1), new XAttribute("max", i + 1), new XAttribute("width", c.Location ? Math.Max(7, c.Header.Length * 2 + 2) : Prototype(c).Width), new XAttribute("customWidth", 1)))));
        root.Element(Ns + "mergeCells")?.Remove();
        data.AddAfterSelf(new XElement(Ns + "mergeCells", new XAttribute("count", merges.Count), merges.Select(m => new XElement(Ns + "mergeCell", new XAttribute("ref", m)))));
        root.Element(Ns + "dimension")?.SetAttributeValue("ref", $"A1:{ColumnName(last)}{lastOutputRow}");
        var view = root.Element(Ns + "sheetViews")?.Element(Ns + "sheetView");
        if (view is not null) { view.Elements().Where(e => e.Name == Ns + "pane" || e.Name == Ns + "selection").Remove(); view.AddFirst(new XElement(Ns + "pane", new XAttribute("xSplit", 3), new XAttribute("ySplit", 4), new XAttribute("topLeftCell", "D5"), new XAttribute("activePane", "bottomRight"), new XAttribute("state", "frozen"))); view.SetAttributeValue("tabSelected", 1); }
        root.Elements().Where(e => e.Name == Ns + "conditionalFormatting" || e.Name == Ns + "dataValidations" || e.Name == Ns + "autoFilter").Remove();
        var workbook = Read("xl/workbook.xml"); var sheets = workbook.Root!.Element(Ns + "sheets")!;
        var guide = sheets.Elements().Single(s => (string?)s.Attribute("name") == "攻略数据");
        var keepId = (string)guide.Attribute(Rel + "id")!; sheets.Elements().Where(s => !ReferenceEquals(s, guide)).Remove();
        workbook.Root.Element(Ns + "definedNames")?.Remove();
        workbook.Root.Element(Ns + "bookViews")?.Elements().ToList().ForEach(v => v.SetAttributeValue("activeTab", 0));
        var rels = Read("xl/_rels/workbook.xml.rels"); var removed = new HashSet<string> { "xl/calcChain.xml" };
        foreach (var relation in rels.Root!.Elements().ToArray())
            if (((string?)relation.Attribute("Type"))?.EndsWith("/worksheet", StringComparison.Ordinal) == true && (string?)relation.Attribute("Id") != keepId || ((string?)relation.Attribute("Type"))?.EndsWith("/calcChain", StringComparison.Ordinal) == true)
            { removed.Add(new Uri(new Uri("https://template.local/xl/workbook.xml"), (string)relation.Attribute("Target")!).AbsolutePath.TrimStart('/')); relation.Remove(); }
        var types = Read("[Content_Types].xml"); types.Root!.Elements().Where(e => removed.Contains(((string?)e.Attribute("PartName"))?.TrimStart('/') ?? "")).Remove();
        var replacements = new Dictionary<string, XDocument> { [mapping.SheetEntryPath] = worksheet, ["xl/styles.xml"] = styles, ["xl/workbook.xml"] = workbook, ["xl/_rels/workbook.xml.rels"] = rels, ["[Content_Types].xml"] = types };
        using var target = ZipFile.Open(output, ZipArchiveMode.Create);
        foreach (var entry in source.Entries)
        {
            token.ThrowIfCancellationRequested(); if (removed.Contains(entry.FullName)) continue;
            var destination = target.CreateEntry(entry.FullName, CompressionLevel.Optimal); using var stream = destination.Open();
            if (replacements.TryGetValue(entry.FullName, out var xml)) xml.Save(stream); else { using var input = entry.Open(); input.CopyTo(stream); }
        }
    }

    public static void Verify(string path, int expectedNodes)
    {
        using var archive = ZipFile.OpenRead(path);
        XDocument Xml(string name) { using var input = archive.GetEntry(name)?.Open() ?? throw new InvalidDataException("导出缺少工作簿文件。"); return XDocument.Load(input); }
        var sheets = Xml("xl/workbook.xml").Descendants(Ns + "sheet").ToArray();
        if (sheets.Length != 1 || (string?)sheets[0].Attribute("name") != "攻略数据") throw new InvalidDataException("导出工作表结构不正确。");
        var id = (string)sheets[0].Attribute(Rel + "id")!;
        var relation = Xml("xl/_rels/workbook.xml.rels").Root!.Elements().Single(r => (string?)r.Attribute("Id") == id);
        var sheetPath = new Uri(new Uri("https://template.local/xl/workbook.xml"), (string)relation.Attribute("Target")!).AbsolutePath.TrimStart('/');
        var data = Xml(sheetPath); var rows = data.Descendants(Ns + "row").ToDictionary(r => (int)r.Attribute("r")!);
        if (Enumerable.Range(5, expectedNodes).Any(r => !rows.ContainsKey(r))) throw new InvalidDataException("导出攻略行数不完整。");
        if (data.Descendants(Ns + "f").Any()) throw new InvalidDataException("导出包含未计算的公式。");
        var styleCount = Xml("xl/styles.xml").Root!.Element(Ns + "cellXfs")!.Elements().Count();
        if (data.Descendants(Ns + "c").Any(c => ((int?)c.Attribute("s") ?? 0) >= styleCount)) throw new InvalidDataException("导出样式引用无效。");
    }
    public static string ColumnName(int column)
    {
        var result = ""; while (column > 0) { column--; result = (char)('A' + column % 26) + result; column /= 26; } return result;
    }
}
