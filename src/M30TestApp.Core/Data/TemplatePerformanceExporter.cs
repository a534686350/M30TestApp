using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.TaskScript;

namespace M30TestApp.Core.Data;

/// <summary>
/// 将 M30 全性能矩阵写入现场提供的“全性能.xlsx”模板。
/// 直接修改模板中的 worksheet XML，保留原有样式、合并单元格、冻结窗格和公式。
/// </summary>
public static class TemplatePerformanceExporter
{
    private const string TemplateFileName = "全性能.xlsx";
    private const string TargetSheetName = "新晶圆性能测试 Wafer performance test";
    private const int DataStartRow = 4;
    private const int TemplateDataRow = 4;
    private const int MaxColumn = 51; // AY
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly Regex FormulaRowRegex = new(@"(?<column>\$?[A-Z]{1,3})(?<row>\$?)4\b", RegexOptions.Compiled);

    public static bool CanExport(TestPlan plan) =>
        string.Equals(plan.FolderName, "M30测试", StringComparison.OrdinalIgnoreCase) &&
        plan.TempPoints.Count >= 3 &&
        plan.PressurePoints.Count >= 3;

    public static string? ResolveTemplatePath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppPaths.BaseDir, "保存数据格式", TemplateFileName),
            Path.Combine(AppContext.BaseDirectory, "保存数据格式", TemplateFileName),
            Path.Combine(Environment.CurrentDirectory, "保存数据格式", TemplateFileName),
        };

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "保存数据格式", TemplateFileName));

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    public static void Export(TaskContext ctx, string outputPath, string? templatePath = null)
    {
        templatePath ??= ResolveTemplatePath();
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
            throw new FileNotFoundException($"未找到全性能模板：{TemplateFileName}", templatePath);

        var tempPath = outputPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (File.Exists(tempPath)) File.Delete(tempPath);

        try
        {
            File.Copy(templatePath, tempPath, overwrite: true);
            using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Update))
            {
                var sheetEntry = FindTargetSheet(zip);
                if (sheetEntry is null)
                    throw new InvalidDataException($"模板中未找到工作表：{TargetSheetName}");

                XDocument sheet;
                using (var stream = sheetEntry.Open())
                    sheet = XDocument.Load(stream, LoadOptions.PreserveWhitespace);

                WriteSheet(ctx, sheet);

                sheetEntry.Delete();
                var replacement = zip.CreateEntry(sheetEntry.FullName, CompressionLevel.Optimal);
                using var output = replacement.Open();
                using var writer = new StreamWriter(output, new UTF8Encoding(false));
                sheet.Save(writer, SaveOptions.DisableFormatting);

                UpdateCalculationMode(zip);
            }

            File.Move(tempPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static ZipArchiveEntry? FindTargetSheet(ZipArchive zip)
    {
        var workbookEntry = zip.GetEntry("xl/workbook.xml");
        var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbookEntry is null || relsEntry is null) return null;

        XDocument workbook;
        XDocument rels;
        using (var stream = workbookEntry.Open()) workbook = XDocument.Load(stream);
        using (var stream = relsEntry.Open()) rels = XDocument.Load(stream);

        var relNs = (XNamespace)"http://schemas.openxmlformats.org/package/2006/relationships";
        var workbookRelNs = (XNamespace)"http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var sheet = workbook.Root?
            .Element(Ns + "sheets")?
            .Elements(Ns + "sheet")
            .FirstOrDefault(x =>
                string.Equals((string?)x.Attribute("name"), TargetSheetName, StringComparison.OrdinalIgnoreCase) ||
                ((string?)x.Attribute("name"))?.Contains("Wafer performance test", StringComparison.OrdinalIgnoreCase) == true);
        if (sheet is null) return null;

        var relId = (string?)sheet.Attribute(workbookRelNs + "id");
        var target = rels.Root?
            .Elements(relNs + "Relationship")
            .FirstOrDefault(x => string.Equals((string?)x.Attribute("Id"), relId, StringComparison.OrdinalIgnoreCase))?
            .Attribute("Target")?.Value;
        if (string.IsNullOrWhiteSpace(target)) return null;

        var normalized = target.Replace('\\', '/').TrimStart('/');
        if (!normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
            normalized = "xl/" + normalized;
        return zip.GetEntry(normalized);
    }

    private static void WriteSheet(TaskContext ctx, XDocument document)
    {
        var root = document.Root ?? throw new InvalidDataException("模板 worksheet 缺少根节点");
        var sheetData = root.Element(Ns + "sheetData") ?? throw new InvalidDataException("模板 worksheet 缺少 sheetData");
        var templateRow = sheetData.Elements(Ns + "row")
            .FirstOrDefault(x => (int?)x.Attribute("r") == TemplateDataRow)
            ?? throw new InvalidDataException($"模板缺少样例数据行：{TemplateDataRow}");

        foreach (var row in sheetData.Elements(Ns + "row").Where(x => (int?)x.Attribute("r") >= DataStartRow).ToList())
            row.Remove();

        var slots = ctx.Slots.Entries
            .OrderBy(s => SlotDacAddress.ParseSlotIndex(s.Slot))
            .ToList();
        var rows = new List<XElement>(slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            var rowNumber = DataStartRow + i;
            var row = new XElement(templateRow);
            row.SetAttributeValue("r", rowNumber.ToString(CultureInfo.InvariantCulture));

            foreach (var cell in row.Elements(Ns + "c").ToList())
            {
                var column = CellColumn((string?)cell.Attribute("r"));
                cell.SetAttributeValue("r", $"{column}{rowNumber}");
                AdjustFormulaRow(cell, rowNumber);
            }

            WriteRow(ctx, row, slots[i], rowNumber);
            rows.Add(row);
        }

        sheetData.Add(rows);
        var dimension = root.Element(Ns + "dimension");
        dimension?.SetAttributeValue("ref", $"A1:AY{Math.Max(DataStartRow, DataStartRow + slots.Count - 1)}");
    }

    private static void WriteRow(TaskContext ctx, XElement row, SlotEntry slot, int rowNumber)
    {
        var tempPoints = ctx.Plan.TempPoints.Take(3).ToArray();
        var pressurePoints = ctx.Plan.PressurePoints.Take(3).ToArray();

        WriteText(row, "A", slotNumber(slot));
        WriteText(row, "B", slot.SerialNo);
        WriteText(row, "C", "");
        WriteText(row, "D", "");
        WriteText(row, "E", "");

        for (var ti = 0; ti < 3; ti++)
        {
            var baseColumn = 6 + ti * 10;
            if (ti >= tempPoints.Length) continue;

            var temp = tempPoints[ti];
            WriteMatrix(row, ColumnName(baseColumn + 0), ctx, slot, $"{temp.Name}_USC");
            WriteMatrix(row, ColumnName(baseColumn + 1), ctx, slot, $"{temp.Name}_ISC");
            WriteMatrix(row, ColumnName(baseColumn + 2), ctx, slot, UsgKey(temp, pressurePoints, 0, reverse: false));
            WriteMatrix(row, ColumnName(baseColumn + 3), ctx, slot, $"{temp.Name}_UT");
            WriteMatrix(row, ColumnName(baseColumn + 4), ctx, slot, UsgKey(temp, pressurePoints, 1, reverse: false));
            WriteMatrix(row, ColumnName(baseColumn + 5), ctx, slot, UsgKey(temp, pressurePoints, 2, reverse: false));
            WriteMatrix(row, ColumnName(baseColumn + 6), ctx, slot, UsgKey(temp, pressurePoints, 1, reverse: true));
            WriteMatrix(row, ColumnName(baseColumn + 7), ctx, slot, UsgKey(temp, pressurePoints, 0, reverse: true));
            WriteMatrix(row, ColumnName(baseColumn + 8), ctx, slot, $"{temp.Name}_UT");
            WriteMatrix(row, ColumnName(baseColumn + 9), ctx, slot, $"{temp.Name}_OvenTemp");
        }

        // AJ:AS are intentionally kept as the template formulas. Excel/WPS will
        // recalculate them from the raw measurements after opening the file.
        WriteText(row, "AT", GetResult(ctx, slot.Slot));

        WriteNumber(row, "AV", pressurePoints.ElementAtOrDefault(0)?.Value);
        WriteNumber(row, "AW", pressurePoints.ElementAtOrDefault(1)?.Value);
        WriteNumber(row, "AX", pressurePoints.ElementAtOrDefault(2)?.Value);
        WriteText(row, "AU", ctx.LeakCheckNote);
        // AY is a template formula: T3 Usource / T3 Isource.
    }

    private static string UsgKey(TempPoint temp, IReadOnlyList<PressurePoint> pressures, int index, bool reverse)
    {
        if (index < 0 || index >= pressures.Count) return "";
        return $"{temp.Name}{pressures[index].Name}_USG{(reverse ? "_R" : "")}";
    }

    private static void WriteMatrix(XElement row, string column, TaskContext ctx, SlotEntry slot, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            WriteText(row, column, "");
            return;
        }

        var cell = ctx.Matrix.Get(slot.Slot, key);
        if (cell is null || !TryParseFinite(cell.Value, out var value))
        {
            WriteText(row, column, "");
            return;
        }

        WriteNumber(row, column, value);
    }

    private static string GetResult(TaskContext ctx, string slot)
    {
        var checks = new (string Code, SpecRange Range)[]
        {
            ("Offset", ctx.Plan.Specs.Offset),
            ("Span", ctx.Plan.Specs.Span),
            ("NL", ctx.Plan.Specs.Linearity),
            ("TCO", ctx.Plan.Specs.TCO),
            ("TCS", ctx.Plan.Specs.TCS),
            ("TCR", ctx.Plan.Specs.TCR),
            ("THO", ctx.Plan.Specs.THO),
            ("THS", ctx.Plan.Specs.THS),
            ("PH", ctx.Plan.Specs.PressureHysteresis),
            ("TCT", ctx.Plan.Specs.CT),
        };

        foreach (var (code, range) in checks)
        {
            if (!ctx.Plan.IsMetricEnabled(code) || !range.HasLimits) continue;
            var value = ctx.Matrix.Get(slot, code)?.Value;
            if (!TryParseFinite(value, out var numeric) || !range.IsInRange(numeric))
                return "fail";
        }

        return "pass";
    }

    private static bool TryParseFinite(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value);

    private static void WriteText(XElement row, string column, string? value)
    {
        var cell = FindOrCreateCell(row, column);
        cell.RemoveNodes();
        cell.SetAttributeValue("t", "inlineStr");
        cell.Add(new XElement(Ns + "is", new XElement(Ns + "t", value ?? "")));
    }

    private static void WriteNumber(XElement row, string column, double? value)
    {
        var cell = FindOrCreateCell(row, column);
        cell.RemoveNodes();
        if (value is null || !double.IsFinite(value.Value))
        {
            cell.SetAttributeValue("t", "inlineStr");
            cell.Add(new XElement(Ns + "is", new XElement(Ns + "t", "")));
            return;
        }

        cell.Attribute("t")?.Remove();
        cell.Add(new XElement(Ns + "v", value.Value.ToString("G17", CultureInfo.InvariantCulture)));
    }

    private static XElement FindOrCreateCell(XElement row, string column)
    {
        var rowNumber = (string?)row.Attribute("r") ?? "1";
        var cell = row.Elements(Ns + "c")
            .FirstOrDefault(x => string.Equals(CellColumn((string?)x.Attribute("r")), column, StringComparison.OrdinalIgnoreCase));
        if (cell is not null) return cell;

        cell = new XElement(Ns + "c", new XAttribute("r", $"{column}{rowNumber}"));
        row.Add(cell);
        return cell;
    }

    private static void AdjustFormulaRow(XElement cell, int rowNumber)
    {
        var formula = cell.Element(Ns + "f");
        if (formula is null) return;
        formula.Value = FormulaRowRegex.Replace(formula.Value, m =>
            $"{m.Groups["column"].Value}{m.Groups["row"].Value}{rowNumber}");
        cell.Element(Ns + "v")?.Remove();
    }

    private static void UpdateCalculationMode(ZipArchive zip)
    {
        var workbookEntry = zip.GetEntry("xl/workbook.xml");
        if (workbookEntry is null) return;

        XDocument workbook;
        using (var stream = workbookEntry.Open())
            workbook = XDocument.Load(stream, LoadOptions.PreserveWhitespace);

        var root = workbook.Root;
        if (root is null) return;
        var calc = root.Element(Ns + "calcPr");
        if (calc is null)
        {
            calc = new XElement(Ns + "calcPr");
            root.Add(calc);
        }

        calc.SetAttributeValue("fullCalcOnLoad", "1");
        calc.SetAttributeValue("forceFullCalc", "1");
        calc.SetAttributeValue("calcMode", "auto");

        workbookEntry.Delete();
        var replacement = zip.CreateEntry(workbookEntry.FullName, CompressionLevel.Optimal);
        using var output = replacement.Open();
        using var writer = new StreamWriter(output, new UTF8Encoding(false));
        workbook.Save(writer, SaveOptions.DisableFormatting);
    }

    private static string slotNumber(SlotEntry slot) =>
        SlotDacAddress.ParseSlotIndex(slot.Slot).ToString(CultureInfo.InvariantCulture);

    private static string CellColumn(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return "";
        return new string(reference.TakeWhile(char.IsLetter).ToArray());
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
}
