using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.TaskScript;

namespace M30TestApp.Core.Data;

/// <summary>报表模板种类：自动测试沿用「全性能.xlsx」；晶圆测试取现场「生成的数据格式」下的样例。</summary>
public enum ReportTemplateMode
{
    /// <summary>自动测试模板（现场既有的全性能.xlsx，行为保持不变）。</summary>
    Auto,

    /// <summary>晶圆测试模板（生成的数据格式 目录下的现场样例报表）。</summary>
    Wafer,
}

/// <summary>
/// 将 M30 全性能矩阵写入现场提供的“全性能.xlsx”模板（新晶圆性能测 Wafer performance test）。
/// 直接修改模板中的 worksheet XML，保留原有样式、合并单元格和公式。
///
/// 每个温度点占 7 列：
///   R(Ω) | Usig0(mV) | Usig50(mV) | Usig100(mV) | Usig50(mV)↓ | Usig0(mV)↓ | Temp(℃)
/// 其中 R(Ω) = Usource / Isource（桥阻），由板卡采集的 USC / ISC 计算。
/// 三个温度点分别落在 F:L / M:S / T:Z。
/// AA:AI 保留模板公式（Offset/Span/Linearity/TCO/TCS/TCR/THO/THS/PH），
/// AJ 写合格判定，AL/AM/AN 写 P0/P50/P100，AO 写测试设备。
/// </summary>
public static class TemplatePerformanceExporter
{
    private const string TemplateFileName = "全性能.xlsx";
    /// <summary>现场样例目录：模板缺失时从这里取最新的 xlsx 作为模板。</summary>
    private const string SampleFolderName = "生成的数据格式";
    private const string TargetSheetName = "新晶圆性能测Wafer performance test";
    private const int DataStartRow = 4;
    private const int TemplateDataRow = 4;
    /// <summary>每个温度点占用的列数。</summary>
    private const int ColumnsPerTemp = 7;
    /// <summary>最后一个有效列：AO（41）。</summary>
    private const int MaxColumn = 41;
    private const string ResultColumn = "AJ";
    private const string DeviceColumn = "AO";
    private const string WaferMergeRef = "C4:C8";
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly Regex FormulaRowRegex = new(@"(?<column>\$?[A-Z]{1,3})(?<row>\$?)4\b", RegexOptions.Compiled);

    public static bool CanExport(TestPlan plan) =>
        string.Equals(plan.FolderName, "M30测试", StringComparison.OrdinalIgnoreCase) &&
        plan.TempPoints.Count >= 3 &&
        plan.PressurePoints.Count >= 3;

    /// <summary>
    /// 运行设置窗口选择的报表模板：
    ///   Auto  = 自动测试模板（沿用 &lt;保存数据格式&gt;/全性能.xlsx）；
    ///   Wafer = 晶圆测试模板（用 &lt;保存数据格式&gt;/生成的数据格式 下的现场样例当模板）。
    /// </summary>
    public static ReportTemplateMode ResolveTemplateMode(IniFile settings) =>
        ParseTemplateMode(settings.Get("Report", "Template", "Auto"));

    public static ReportTemplateMode ParseTemplateMode(string? text) =>
        string.Equals((text ?? "").Trim(), nameof(ReportTemplateMode.Wafer), StringComparison.OrdinalIgnoreCase)
            ? ReportTemplateMode.Wafer
            : ReportTemplateMode.Auto;

    public static string TemplateModeKey(ReportTemplateMode mode) => mode.ToString();

    /// <summary>默认（自动测试模板）的模板路径。</summary>
    public static string? ResolveTemplatePath() => ResolveTemplatePath(ReportTemplateMode.Auto);

    /// <summary>按模板模式解析模板路径，首选缺失时回退到另一路，保证现场总有模板可用。</summary>
    public static string? ResolveTemplatePath(ReportTemplateMode mode)
    {
        var roots = SearchRoots().ToList();

        if (mode == ReportTemplateMode.Wafer)
        {
            foreach (var root in roots)
            {
                var sample = NewestSampleTemplate(Path.Combine(root, "保存数据格式", SampleFolderName));
                if (sample is not null) return sample;
            }
        }

        var hit = roots
            .Select(root => Path.Combine(root, "保存数据格式", TemplateFileName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
        if (hit is not null) return hit;

        foreach (var root in roots)
        {
            var sample = NewestSampleTemplate(Path.Combine(root, "保存数据格式", SampleFolderName));
            if (sample is not null) return sample;
        }

        return null;
    }

    private static IEnumerable<string> SearchRoots()
    {
        yield return AppPaths.BaseDir;
        yield return AppContext.BaseDirectory;
        yield return Environment.CurrentDirectory;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            yield return dir.FullName;
    }

    private static string? NewestSampleTemplate(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        try
        {
            return Directory.EnumerateFiles(folder, "*.xlsx")
                .Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
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
        var lastRow = Math.Max(DataStartRow, DataStartRow + slots.Count - 1);
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

            WriteRow(ctx, row, slots[i], rowNumber, isAnchorRow: i == 0);
            rows.Add(row);
        }

        sheetData.Add(rows);
        var dimensionLastRow = slots.Count == 0 ? DataStartRow - 1 : lastRow;
        root.Element(Ns + "dimension")?.SetAttributeValue("ref", $"A1:{ColumnName(MaxColumn)}{dimensionLastRow}");
        UpdateWaferMerge(root, lastRow);
        UpdateSpecFormatting(root, ctx, slots.Count == 0 ? 0 : lastRow);
    }

    /// <summary>模板里 C4:C8 是晶圆编号的纵向合并，数据行数变化时同步调整。</summary>
    private static void UpdateWaferMerge(XElement root, int lastRow)
    {
        var mergeCells = root.Element(Ns + "mergeCells");
        if (mergeCells is null) return;

        var waferMerge = mergeCells.Elements(Ns + "mergeCell")
            .FirstOrDefault(x => string.Equals((string?)x.Attribute("ref"), WaferMergeRef, StringComparison.OrdinalIgnoreCase));
        if (waferMerge is null) return;

        if (lastRow <= DataStartRow)
            waferMerge.Remove();
        else
            waferMerge.SetAttributeValue("ref", $"C{DataStartRow}:C{lastRow}");

        mergeCells.SetAttributeValue("count", mergeCells.Elements(Ns + "mergeCell").Count().ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteRow(TaskContext ctx, XElement row, SlotEntry slot, int rowNumber, bool isAnchorRow)
    {
        var tempPoints = ctx.Plan.TempPoints.Take(3).ToArray();
        var pressures = ctx.Plan.PressurePoints.ToArray();
        var p0 = pressures.ElementAtOrDefault(0);
        var p50 = pressures.Length >= 2 ? pressures[pressures.Length / 2] : null;
        var p100 = pressures.Length >= 2 ? pressures[^1] : null;

        WriteNumber(row, "A", slotIndex(slot));
        WriteText(row, "B", slot.SerialNo);
        // C 列在模板中是合并单元格（C4:Cn），只在合并区左上角写一次，其余行移除该单元格避免合并区内出现值。
        if (isAnchorRow)
            WriteText(row, "C", WaferNumber(ctx, slot));
        else
            FindOrCreateCell(row, "C").Remove();
        // D/E = 芯片坐标，来自工位对应表手动录入（留空则单元格为空，可后续在表里补录）。
        WriteCoordinate(row, "D", slot.PositionX);
        WriteCoordinate(row, "E", slot.PositionY);

        for (var ti = 0; ti < 3; ti++)
        {
            var baseColumn = 6 + ti * ColumnsPerTemp;
            if (ti >= tempPoints.Length) continue;

            var temp = tempPoints[ti];
            WriteResistance(row, ColumnName(baseColumn + 0), ctx, slot, temp);
            WriteMatrix(row, ColumnName(baseColumn + 1), ctx, slot, UsgKey(temp, p0, reverse: false));
            WriteMatrix(row, ColumnName(baseColumn + 2), ctx, slot, UsgKey(temp, p50, reverse: false));
            WriteMatrix(row, ColumnName(baseColumn + 3), ctx, slot, UsgKey(temp, p100, reverse: false));
            WriteMatrix(row, ColumnName(baseColumn + 4), ctx, slot, UsgKey(temp, p50, reverse: true));
            WriteMatrix(row, ColumnName(baseColumn + 5), ctx, slot, UsgKey(temp, p0, reverse: true));
            WriteMatrix(row, ColumnName(baseColumn + 6), ctx, slot, $"{temp.Name}_OvenTemp");
        }

        // AA:AI 保持模板公式，Excel/WPS 打开时按原始测量值重算。
        WriteText(row, ResultColumn, GetResult(ctx, slot.Slot));

        WriteNumber(row, "AL", p0?.Value);
        WriteNumber(row, "AM", p50?.Value);
        WriteNumber(row, "AN", p100?.Value);
        WriteText(row, DeviceColumn, DeviceName(ctx));
    }

    /// <summary>坐标列：能解析成数字就写数字（与模板单元格格式一致），否则原样写文本，空值留空。</summary>
    private static void WriteCoordinate(XElement row, string column, string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            WriteText(row, column, "");
            return;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) &&
            double.IsFinite(numeric))
            WriteNumber(row, column, numeric);
        else
            WriteText(row, column, value);
    }

    /// <summary>R(Ω) = Usource / Isource。</summary>
    private static void WriteResistance(XElement row, string column, TaskContext ctx, SlotEntry slot, TempPoint temp)
    {
        var usource = Read(ctx, slot.Slot, $"{temp.Name}_USC");
        var isource = Read(ctx, slot.Slot, $"{temp.Name}_ISC");
        if (double.IsNaN(usource) || double.IsNaN(isource) || isource == 0)
        {
            WriteText(row, column, "");
            return;
        }

        WriteNumber(row, column, usource / isource);
    }

    private static string UsgKey(TempPoint temp, PressurePoint? pressure, bool reverse) =>
        pressure is null ? "" : $"{temp.Name}{pressure.Name}_USG{(reverse ? "_R" : "")}";

    private static void WriteMatrix(XElement row, string column, TaskContext ctx, SlotEntry slot, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            WriteText(row, column, "");
            return;
        }

        var cell = ctx.Matrix.Get(slot.Slot, key);
        if (cell is null)
        {
            WriteText(row, column, "");
            return;
        }

        // 报表需要完整精度：Cell.Value 只有 6 位有效数字，优先取原始采集值。
        if (double.IsFinite(cell.RawValue))
        {
            WriteNumber(row, column, cell.RawValue);
            return;
        }

        if (!TryParseFinite(cell.Value, out var value))
        {
            WriteText(row, column, "");
            return;
        }

        WriteNumber(row, column, value);
    }

    private static double Read(TaskContext ctx, string slot, string key)
    {
        var cell = ctx.Matrix.Get(slot, key);
        if (cell is null) return double.NaN;
        if (double.IsFinite(cell.RawValue)) return cell.RawValue;
        return TryParseFinite(cell.Value, out var value) ? value : double.NaN;
    }

    /// <summary>
    /// 晶圆编号（C 列）：优先取运行设置窗口录入的 [Report] WaferNo，
    /// 留空则用序列号去掉最后一段工位序号自动推导。
    /// </summary>
    private static string WaferNumber(TaskContext ctx, SlotEntry slot)
    {
        var configured = ctx.Settings.Get("Report", "WaferNo", "").Trim();
        var wafer = configured.Length > 0 ? configured : StripSlotSuffix(slot.SerialNo ?? "");
        var model = ResolveModelName(ctx);
        if (string.IsNullOrWhiteSpace(wafer)) return model;
        if (string.IsNullOrWhiteSpace(model)) return wafer;
        return $"{wafer}\n{model}";
    }

    private static string StripSlotSuffix(string serial)
    {
        var text = serial.Trim();
        var idx = text.LastIndexOf('-');
        if (idx <= 0 || idx == text.Length - 1) return text;
        var tail = text[(idx + 1)..];
        return tail.All(char.IsDigit) ? text[..idx] : text;
    }

    /// <summary>
    /// 报表型号：优先取运行设置窗口录入的 [Report] Model，
    /// 留空则用方案传感器型号去掉 M30- 前缀（现场报表不带该前缀）。
    /// </summary>
    public static string ResolveModelName(TaskContext ctx)
    {
        var configured = ctx.Settings.Get("Report", "Model", "").Trim();
        return configured.Length > 0 ? configured : ModelName(ctx.Plan.SensorType);
    }

    /// <summary>型号：去掉方案里 M30- 前缀，现场报表使用不带前缀的型号名。</summary>
    public static string ModelName(string? sensorType)
    {
        var text = (sensorType ?? "").Trim();
        return text.StartsWith("M30-", StringComparison.OrdinalIgnoreCase) ? text[4..] : text;
    }

    private static string DeviceName(TaskContext ctx)
    {
        // 开测前在运行设置窗口录入；留空回落到压力控制器型号。
        var configured = ctx.Settings.Get("Report", "DeviceName", "").Trim();
        return configured.Length > 0 ? configured : ctx.Settings.Get("Device.Pressure", "Model", "").Trim();
    }

    /// <summary>AA:AI 列对应的指标代码，顺序与模板表头一致。</summary>
    private static readonly (int Column, string Code)[] SpecColumns =
    {
        (27, "Offset"),              // AA
        (28, "Span"),                // AB
        (29, "NL"),                  // AC Linearity
        (30, "TCO"),                 // AD
        (31, "TCS"),                 // AE
        (32, "TCR"),                 // AF
        (33, "THO"),                 // AG
        (34, "THS"),                 // AH
        (35, "PH"),                  // AI Pressure hysteresis
    };

    /// <summary>
    /// 把模板里 AA:AI 数据区的条件格式限值换成当前方案的指标限值。
    /// 模板自带的是样例型号的硬编码限值，换型号后判定颜色会错；这里按程序指标计算口径重写。
    /// 指标未启用或无上下限时不加规则（不标色）。
    /// </summary>
    private static void UpdateSpecFormatting(XElement root, TaskContext ctx, int lastRow)
    {
        var containers = root.Elements(Ns + "conditionalFormatting").ToList();
        if (containers.Count == 0) return;

        foreach (var container in containers)
        {
            if (!IsSpecRange((string?)container.Attribute("sqref"))) continue;
            foreach (var rule in container.Elements(Ns + "cfRule").Where(IsNotBetweenRule).ToList())
                rule.Remove();
            if (!container.Elements(Ns + "cfRule").Any())
                container.Remove();
        }

        if (lastRow < DataStartRow) return;

        var remaining = root.Elements(Ns + "conditionalFormatting").ToList();
        var priority = remaining
            .SelectMany(c => c.Elements(Ns + "cfRule"))
            .Select(r => (int?)r.Attribute("priority") ?? 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        XElement? anchor = remaining.LastOrDefault();
        foreach (var (column, code) in SpecColumns)
        {
            if (!ctx.Plan.IsMetricEnabled(code)) continue;
            var spec = ctx.Plan.Specs[code];
            var min = spec.MinVal;
            var max = spec.MaxVal;
            if (min is null && max is null) continue;
            if (min is not null && max is not null && min > max) (min, max) = (max, min);
            min ??= max;
            max ??= min;

            var name = ColumnName(column);
            var element = new XElement(Ns + "conditionalFormatting",
                new XAttribute("sqref", $"{name}{DataStartRow}:{name}{lastRow}"),
                new XElement(Ns + "cfRule",
                    new XAttribute("type", "cellIs"),
                    new XAttribute("dxfId", "0"),
                    new XAttribute("priority", priority.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("operator", "notBetween"),
                    new XElement(Ns + "formula", min!.Value.ToString("G17", CultureInfo.InvariantCulture)),
                    new XElement(Ns + "formula", max!.Value.ToString("G17", CultureInfo.InvariantCulture))));
            priority++;

            if (anchor is null)
            {
                // 模板没有任何条件格式时，按 schema 顺序插到 mergeCells 之后。
                var mergeCells = root.Element(Ns + "mergeCells");
                if (mergeCells is not null) mergeCells.AddAfterSelf(element);
                else root.Element(Ns + "sheetData")?.AddAfterSelf(element);
                anchor = element;
            }
            else
            {
                anchor.AddAfterSelf(element);
                anchor = element;
            }
        }
    }

    private static bool IsNotBetweenRule(XElement rule) =>
        string.Equals((string?)rule.Attribute("type"), "cellIs", StringComparison.OrdinalIgnoreCase) &&
        string.Equals((string?)rule.Attribute("operator"), "notBetween", StringComparison.OrdinalIgnoreCase);

    /// <summary>判断 sqref 是否完全落在数据区的 AA:AI 九列指标格上。</summary>
    private static bool IsSpecRange(string? sqref)
    {
        if (string.IsNullOrWhiteSpace(sqref)) return false;
        foreach (var part in sqref.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split(':');
            if (pieces.Length is < 1 or > 2) return false;
            if (!TryParseCellRef(pieces[0], out var firstColumn, out var firstRow)) return false;

            var lastColumn = firstColumn;
            var lastRow = firstRow;
            if (pieces.Length == 2 && !TryParseCellRef(pieces[1], out lastColumn, out lastRow)) return false;

            if (Math.Min(firstColumn, lastColumn) < SpecColumns[0].Column) return false;
            if (Math.Max(firstColumn, lastColumn) > SpecColumns[^1].Column) return false;
            if (Math.Min(firstRow, lastRow) < DataStartRow) return false;
        }
        return true;
    }

    private static bool TryParseCellRef(string reference, out int column, out int row)
    {
        column = 0;
        row = 0;
        if (string.IsNullOrWhiteSpace(reference)) return false;

        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray());
        var digits = reference[letters.Length..];
        if (letters.Length == 0 || digits.Length == 0) return false;
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out row)) return false;

        column = ColumnIndex(letters);
        return column > 0;
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
        var index = ColumnIndex(column);
        var before = row.Elements(Ns + "c")
            .FirstOrDefault(x => ColumnIndex(CellColumn((string?)x.Attribute("r"))) > index);
        if (before is null)
            row.Add(cell);
        else
            before.AddBeforeSelf(cell);
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

    private static int slotIndex(SlotEntry slot) => SlotDacAddress.ParseSlotIndex(slot.Slot);

    private static string CellColumn(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return "";
        return new string(reference.TakeWhile(char.IsLetter).ToArray());
    }

    private static int ColumnIndex(string column)
    {
        var index = 0;
        foreach (var ch in column)
            index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        return index;
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
