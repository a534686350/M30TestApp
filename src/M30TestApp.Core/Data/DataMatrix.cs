using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace M30TestApp.Core.Data;

public enum CellStatus { Empty, Pending, Ok, Warn, Error }

public sealed class Cell
{
    public string Key { get; init; } = "";    // e.g. "T1P1_Usign"
    public string Value { get; set; } = "";
    public CellStatus Status { get; set; } = CellStatus.Empty;
    public DateTime UpdatedAt { get; set; }
}

public readonly record struct CellUpdate(string Slot, Cell Cell);

/// <summary>
/// Sparse slot × point matrix. Rows keyed by slot (e.g. "Slot1"), columns keyed by
/// point name (e.g. "T1P1_Usign"). Thread-safe; raises CellUpdated for UI binding.
/// </summary>
public sealed class DataMatrix
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Cell>> _rows = new();

    public IReadOnlyCollection<string> Slots => _rows.Keys.ToArray();

    public event EventHandler<CellUpdate>? CellUpdated;

    public void EnsureSlot(string slot) =>
        _rows.GetOrAdd(slot, _ => new ConcurrentDictionary<string, Cell>());

    /// <summary>
    /// Normalize a column key to letters/digits/underscore so it survives WPF binding
    /// path syntax (e.g. "DMM-V" → "DMM_V"; minus sign would be parsed as an operator).
    /// </summary>
    public static string SanitizeKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "_";
        var sb = new System.Text.StringBuilder(key.Length);
        foreach (var c in key)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    public Cell Set(string slot, string key, double value, CellStatus status = CellStatus.Ok)
    {
        key = SanitizeKey(key);
        var row = _rows.GetOrAdd(slot, _ => new ConcurrentDictionary<string, Cell>());
        var cell = row.AddOrUpdate(key,
            _ => new Cell { Key = key, Value = value.ToString("G6", CultureInfo.InvariantCulture), Status = status, UpdatedAt = DateTime.Now },
            (_, c) => { c.Value = value.ToString("G6", CultureInfo.InvariantCulture); c.Status = status; c.UpdatedAt = DateTime.Now; return c; });
        CellUpdated?.Invoke(this, new CellUpdate(slot, cell));
        return cell;
    }

    public Cell? Get(string slot, string key)
    {
        if (_rows.TryGetValue(slot, out var row) && row.TryGetValue(key, out var c))
            return c;
        return null;
    }

    public void Clear()
    {
        _rows.Clear();
    }

    /// <summary>Extract numeric suffix for natural sorting: "Slot2" → (Slot, 2).</summary>
    private static (string prefix, int number) NaturalSortKey(string s)
    {
        var i = s.Length;
        while (i > 0 && char.IsDigit(s[i - 1])) i--;
        var prefix = s[..i];
        var number = i < s.Length && int.TryParse(s[i..], out var n) ? n : 0;
        return (prefix, number);
    }

    public void ExportCsv(string path, IEnumerable<string> orderedColumns,
        IReadOnlyDictionary<string, string>? slotSerialMap = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var cols = orderedColumns.ToArray();
        using var sw = new StreamWriter(path, false, Encoding.UTF8);
        var header = slotSerialMap != null ? "slot,SerialNo," : "slot,";
        sw.WriteLine(header + string.Join(",", cols));
        foreach (var slot in _rows.Keys.OrderBy(k => NaturalSortKey(k)))
        {
            var row = _rows[slot];
            var sb = new StringBuilder(slot);
            if (slotSerialMap != null)
            {
                sb.Append(',');
                if (slotSerialMap.TryGetValue(slot, out var sn)) sb.Append(sn);
            }
            foreach (var col in cols)
            {
                sb.Append(',');
                if (row.TryGetValue(col, out var c)) sb.Append(c.Value);
            }
            sw.WriteLine(sb);
        }
    }

    public void ExportXlsx(string path, IEnumerable<string> orderedColumns,
        IReadOnlyDictionary<string, string>? slotSerialMap = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var cols = orderedColumns.ToArray();
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
                <sheet name="TestData" sheetId="1" r:id="rId1"/>
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

        var headers = slotSerialMap != null
            ? new[] { "slot", "SerialNo" }.Concat(cols).ToArray()
            : new[] { "slot" }.Concat(cols).ToArray();

        WriteXlsxRow(sheet, 1, headers);
        var rowIndex = 2;
        foreach (var slot in _rows.Keys.OrderBy(k => NaturalSortKey(k)))
        {
            var row = _rows[slot];
            var values = new List<string> { slot };
            if (slotSerialMap != null)
                values.Add(slotSerialMap.TryGetValue(slot, out var sn) ? sn : "");
            foreach (var col in cols)
                values.Add(row.TryGetValue(col, out var c) ? c.Value : "");
            WriteXlsxRow(sheet, rowIndex++, values);
        }

        sheet.AppendLine("</sheetData>");
        sheet.AppendLine("</worksheet>");
        WriteZipEntry(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
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
            sheet.Append(CultureInfo.InvariantCulture, $"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t>{XmlEscape(value)}</t></is></c>");
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

    /// <summary>Load matrix cells from a CSV previously written by <see cref="ExportCsv"/>.</summary>
    public void ImportCsv(string path, out IReadOnlyList<string> columns)
    {
        _rows.Clear();
        var colList = new List<string>();
        columns = colList;
        if (!File.Exists(path)) return;

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length < 2) return;

        var headerParts = lines[0].Split(',');
        var dataStart = 1;
        if (headerParts.Length > 1 && headerParts[1].Equals("SerialNo", StringComparison.OrdinalIgnoreCase))
            dataStart = 2;

        for (var i = dataStart; i < headerParts.Length; i++)
        {
            var key = SanitizeKey(headerParts[i].Trim());
            if (!string.IsNullOrEmpty(key)) colList.Add(key);
        }

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < dataStart + 1) continue;
            var slot = parts[0].Trim();
            EnsureSlot(slot);
            for (var ci = 0; ci < colList.Count; ci++)
            {
                var colIndex = dataStart + ci;
                if (colIndex >= parts.Length) break;
                var raw = parts[colIndex].Trim();
                if (string.IsNullOrEmpty(raw)) continue;
                var status = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                    ? (double.IsNaN(v) ? CellStatus.Error : CellStatus.Ok)
                    : CellStatus.Empty;
                Set(slot, colList[ci], double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var num) ? num : double.NaN, status);
            }
        }
    }
}
