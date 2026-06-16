using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using M30TestApp.Core.Config;

namespace M30TestApp.Core.Data;

/// <summary>
/// Exports long-term stability data with two-row grouped headers (temperature / sub-column).
/// </summary>
public static class LongTermStabilityExporter
{
    public static void Export(DataMatrix matrix, TestPlan plan, string path,
        IReadOnlyDictionary<string, string> slotSerialMap,
        LongTermStabilityMeasureMode mode = LongTermStabilityMeasureMode.Voltage)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var columns = LongTermStabilityMatrix.BuildColumns(plan, mode);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        WriteZipEntry(zip, "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        WriteZipEntry(zip, "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteZipEntry(zip, "xl/workbook.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="长期稳定性" sheetId="1" r:id="rId1"/>
              </sheets>
            </workbook>
            """);
        WriteZipEntry(zip, "xl/_rels/workbook.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);

        var sheet = new StringBuilder();
        sheet.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sheet.AppendLine("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        sheet.AppendLine("<sheetData>");

        var row1 = new List<string> { "", "" };
        var row2 = new List<string> { "工位", "序列号" };
        string? currentTempPoint = null;
        foreach (var col in columns)
        {
            row1.Add(col.TempPointName != currentTempPoint ? col.TempLabel : "");
            currentTempPoint = col.TempPointName;
            row2.Add(col.SubLabel);
        }
        WriteXlsxRow(sheet, 1, row1);
        WriteXlsxRow(sheet, 2, row2);

        var cols = columns.Select(c => c.Key).ToArray();
        var rowIndex = 3;
        foreach (var slot in matrix.Slots.OrderBy(k => NaturalSortKey(k)))
        {
            var values = new List<string> { slot };
            values.Add(slotSerialMap.TryGetValue(slot, out var sn) ? sn : "");
            foreach (var col in cols)
            {
                var cell = matrix.Get(slot, col);
                values.Add(cell?.Value ?? "");
            }
            WriteXlsxRow(sheet, rowIndex++, values);
        }

        sheet.AppendLine("</sheetData>");

        var dataStartCol = 3;
        var merges = new List<string> { "A1:A2", "B1:B2" };
        merges.AddRange(BuildMergeCells(columns, dataStartCol));
        sheet.AppendLine("<mergeCells count=\"" + merges.Count + "\">");
        foreach (var m in merges)
            sheet.AppendLine("<mergeCell ref=\"" + m + "\"/>");
        sheet.AppendLine("</mergeCells>");

        sheet.AppendLine("</worksheet>");
        WriteZipEntry(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
    }

    private static string[] BuildMergeCells(IReadOnlyList<LongTermMatrixColumn> columns, int dataStartCol)
    {
        var merges = new List<string>();
        var colIndex = dataStartCol;
        var i = 0;
        while (i < columns.Count)
        {
            var tempPoint = columns[i].TempPointName;
            var start = colIndex;
            while (i < columns.Count && columns[i].TempPointName == tempPoint)
            {
                i++;
                colIndex++;
            }
            var end = colIndex - 1;
            if (end > start)
                merges.Add($"{ColumnName(start)}1:{ColumnName(end)}1");
        }
        return merges.ToArray();
    }

    private static (string prefix, int number) NaturalSortKey(string s)
    {
        var i = s.Length;
        while (i > 0 && char.IsDigit(s[i - 1])) i--;
        var prefix = s[..i];
        var number = i < s.Length && int.TryParse(s[i..], out var n) ? n : 0;
        return (prefix, number);
    }

    private static void WriteZipEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }

    private static void WriteXlsxRow(StringBuilder sheet, int rowIndex, IEnumerable<string> values)
    {
        sheet.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowIndex}\">");
        var colIndex = 1;
        foreach (var value in values)
        {
            var cellRef = $"{ColumnName(colIndex++)}{rowIndex}";
            sheet.Append(CultureInfo.InvariantCulture,
                $"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t>{XmlEscape(value)}</t></is></c>");
        }
        sheet.AppendLine("</row>");
    }

    private static string ColumnName(int index)
    {
        var name = "";
        while (index > 0)
        {
            index--;
            name = (char)('A' + index % 26) + name;
            index /= 26;
        }
        return name;
    }

    private static string XmlEscape(string? value) => (value ?? "")
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
