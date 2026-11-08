using System.IO.Compression;
using System.Text;
using HikingGuide.Infrastructure.Calibration;

namespace HikingGuide.Infrastructure.Tests;

public sealed class CachedWorkbookReaderTests
{
    [Fact]
    public void PreservesCachedValueFormulaFormatAndSourceBytes()
    {
        var path = MakeWorkbook("<c r=\"A2\" s=\"1\"><f>2*2.38</f><v>4.76</v></c>");
        try
        {
            var before = File.ReadAllBytes(path);
            var cell = CachedWorkbookReader.Read(path).Single().Rows.Single().Cells.Single();
            Assert.Equal(4.76, cell.ReferenceValue);
            Assert.Equal("=2*2.38", cell.FormulaText);
            Assert.Equal("A2", cell.CellAddress);
            Assert.Equal("0.0000", cell.NumberFormat);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingCacheAndExcelErrorsAreNotConvertedToZero()
    {
        var path = MakeWorkbook("<c r=\"A2\"><f>1+1</f></c><c r=\"B2\" t=\"e\"><v>#DIV/0!</v></c>");
        try
        {
            var cells = CachedWorkbookReader.Read(path).Single().Rows.Single().Cells;
            Assert.All(cells, c => Assert.Null(c.ReferenceValue));
            Assert.Equal("公式没有缓存结果", cells[0].Error);
            Assert.Equal("#DIV/0!", cells[1].Error);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SharedAndRichStringsPreserveChineseWithoutPhoneticText()
    {
        var path = MakeWorkbook("<c r=\"A2\" t=\"s\"><v>0</v></c><c r=\"B2\" t=\"inlineStr\"><is><r><t>营地</t></r><r><t>甲</t></r></is></c>");
        try
        {
            var cells = CachedWorkbookReader.Read(path).Single().Rows.Single().Cells;
            Assert.Equal("向巴措", cells[0].Text);
            Assert.Equal("营地甲", cells[1].Text);
            Assert.Null(cells[0].ReferenceValue);
        }
        finally { File.Delete(path); }
    }

    private static string MakeWorkbook(string cells, string? rows = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "P9-" + Guid.NewGuid() + ".xlsx");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Write("xl/workbook.xml", $"<workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"参考数据\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Write("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
        Write("xl/styles.xml", $"<styleSheet xmlns=\"{ns}\"><numFmts><numFmt numFmtId=\"164\" formatCode=\"0.0000\"/></numFmts><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"164\"/></cellXfs></styleSheet>");
        Write("xl/sharedStrings.xml", $"<sst xmlns=\"{ns}\"><si><t>向巴措</t><rPh sb=\"0\" eb=\"3\"><t>拼音</t></rPh></si></sst>");
        Write("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{ns}\"><sheetData>{rows ?? $"<row r=\"2\">{cells}</row>"}</sheetData></worksheet>");
        return path;
        void Write(string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }

    [Fact]
    public void SharedFormulaIsExpandedWithoutChangingCachedResultOrAbsoluteReferences()
    {
        var path = MakeWorkbook("<c r=\"Q2\"><f t=\"shared\" si=\"0\" ref=\"Q2:Q3\">J2-$M2-P$2</f><v>1</v></c><c r=\"Q3\"><f t=\"shared\" si=\"0\"/><v>0.5232</v></c>");
        try
        {
            var cell = CachedWorkbookReader.Read(path).Single().Rows.Single().Cells[1];
            Assert.Equal("=J3-$M3-P$2", cell.FormulaText);
            Assert.Equal(0.5232, cell.ReferenceValue);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExtractsReorderedNamedColumnsAndReportsResidualWithoutRepair()
    {
        var headers = "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>位置（标注点）</t></is></c>" +
            "<c r=\"B1\" t=\"inlineStr\"><is><t>平路距离KM</t></is></c>" +
            "<c r=\"C1\" t=\"inlineStr\"><is><t>分段下降距离KM</t></is></c>" +
            "<c r=\"D1\" t=\"inlineStr\"><is><t>分段距离KM</t></is></c>" +
            "<c r=\"E1\" t=\"inlineStr\"><is><t>分段爬升距离KM</t></is></c></row>";
        var rows = headers + "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>0</v></c>" +
            "<c r=\"B2\"><v>0.5</v></c><c r=\"C2\"><v>2</v></c>" +
            "<c r=\"D2\"><f>2*2.38</f><v>4.76</v></c><c r=\"E2\"><v>2</v></c></row>";
        var path = MakeWorkbook("", rows);
        try
        {
            var before = File.ReadAllBytes(path);
            var audit = LocusReferenceExtractor.Read(path);
            var row = Assert.Single(audit.Segments);
            Assert.Equal("向巴措", row.Position);
            Assert.True(row.HasCompleteDistanceTargets);
            Assert.True(row.IsCompositeSuspect);
            Assert.Equal(260, row.ResidualMeters!.Value, 6);
            Assert.Equal(4.76, row.Fields["分段距离KM"].ReferenceValue);
            Assert.NotEmpty(audit.Warnings); // 辅助海拔/高度字段缺失仍被审计。
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }
}
