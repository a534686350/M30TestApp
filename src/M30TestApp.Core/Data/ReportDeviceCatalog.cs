using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;

namespace M30TestApp.Core.Data;

/// <summary>
/// 报表「测试设备」（性能测试报表 AO 列）的候选型号目录。
///
/// 候选来自两路，取并集：
///   1. <see cref="Builtin"/> —— 内置默认（现场 2025-07~12 共 180 份报表 AO 列的实测去重值）；
///   2. 往期报表扫描 —— 只读扫描 <see cref="ResolveScanRoots"/> 下的 *.xlsx，抽取 AO 列去重后合并。
///      扫描结果缓存到 setting/ReportDeviceModels.txt，避免每次开窗口重扫。
///
/// **目标目录始终只读**：本类不创建、不修改、不删除扫描目录下的任何文件。
/// </summary>
public static class ReportDeviceCatalog
{
    /// <summary>内置默认候选（2025-07~202512 现场报表 AO 列实测值）。</summary>
    public static readonly string[] Builtin =
    {
        "6270A BG3.5M",
        "6270A BG7M",
        "6270A BG10K",
        "6270A BG14K",
        "6270A BG100K",
        "6270A 100K",
        "8270A BG3.5M",
        "8270A BG7M",
        "8270A BG28M",
    };

    private const string CacheFileName = "ReportDeviceModels.txt";
    private const string DefaultScanRoot = @"D:\数据整理\202507-202512";
    private const int DataStartRow = 4;
    private const int MaxFiles = 1500;
    private const int MaxCached = 200;

    /// <summary>扫描根目录（setting 里 [Report] DeviceScanDirs 可覆盖，多个用 ; 分隔）。</summary>
    public static IEnumerable<string> ResolveScanRoots(IniFile? settings)
    {
        var configured = settings?.Get("Report", "DeviceScanDirs", "").Trim() ?? "";
        if (configured.Length == 0)
        {
            yield return DefaultScanRoot;
            yield break;
        }

        foreach (var part in configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return part;
    }

    private static string CachePath => Path.Combine(AppPaths.SettingDir, CacheFileName);

    /// <summary>内置默认 + 缓存结果（去重、排序），另有 <paramref name="extra"/> 时一并并入。</summary>
    public static List<string> LoadCached(IniFile? settings, params string?[] extra)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        void Add(string? value)
        {
            var text = (value ?? "").Trim();
            if (text.Length == 0) return;
            if (set.Add(text)) list.Add(text);
        }

        Add(settings?.Get("Report", "DeviceName", ""));
        foreach (var m in Builtin) Add(m);
        foreach (var m in ReadCache()) Add(m);
        foreach (var m in extra) Add(m);

        return list.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ReadCache()
    {
        var result = new List<string>();
        try
        {
            if (!File.Exists(CachePath)) return result;
            foreach (var line in File.ReadAllLines(CachePath, Encoding.UTF8))
            {
                var text = line.Trim();
                if (text.Length == 0 || text.StartsWith('#')) continue;
                result.Add(text);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Report", $"读取设备型号缓存失败: {ex.Message}");
        }
        return result;
    }

    private static void WriteCache(IEnumerable<string> models)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingDir);
            var lines = new List<string>
            {
                "# 报表测试设备候选（往期报表 AO 列扫描结果，程序自动维护，可安全删除）",
                $"# scanned={DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            };
            lines.AddRange(models.Take(MaxCached));
            File.WriteAllLines(CachePath, lines, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Report", $"写入设备型号缓存失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 只读扫描往期报表，抽取「新晶圆性能测」工作表的 AO 列（测试设备）去重值。
    /// 目录不存在 / 无权限时静默返回空集合。耗时较长，请在后台线程调用。
    /// </summary>
    public static List<string> ScanHistoricalDevices(IniFile? settings)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in ResolveScanRoots(settings))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.xlsx", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Report", $"枚举往期报表失败 {root}: {ex.Message}");
                continue;
            }

            var scanned = 0;
            foreach (var file in files)
            {
                if (scanned++ >= MaxFiles) break;
                if (Path.GetFileName(file).StartsWith("~$", StringComparison.Ordinal)) continue;

                var device = TryReadDeviceCell(file);
                if (string.IsNullOrWhiteSpace(device)) continue;
                if (seen.Add(device)) found.Add(device);
            }
        }

        if (found.Count > 0)
        {
            found.Sort(StringComparer.OrdinalIgnoreCase);
            WriteCache(found);
        }
        return found;
    }

    /// <summary>读取一份报表的 AO 列（测试设备）值；失败返回 null。</summary>
    private static string? TryReadDeviceCell(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var sheetPath = ResolveWaferSheet(zip);
            if (sheetPath is null) return null;

            var shared = ReadSharedStrings(zip);
            using var stream = zip.GetEntry(sheetPath)!.Open();
            var doc = XDocument.Load(stream);

            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var sheetData = doc.Root?.Element(ns + "sheetData");
            if (sheetData is null) return null;

            // AO 列固定存放测试设备。第 1~3 行是表头（AO3 就是「测试设备」四个字），
            // 只看数据区（第 4 行起）的第一个非空值，避免把表头当成型号收进候选。
            foreach (var row in sheetData.Elements(ns + "row"))
            {
                if (((int?)row.Attribute("r") ?? 0) < DataStartRow) continue;

                foreach (var cell in row.Elements(ns + "c"))
                {
                    var reference = (string?)cell.Attribute("r") ?? "";
                    if (!reference.StartsWith("AO", StringComparison.OrdinalIgnoreCase)) continue;

                    var value = ReadCellText(cell, ns, shared).Trim();
                    if (value.Length == 0 || IsHeaderText(value)) continue;
                    return value;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Report", $"读取往期报表失败 {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static bool IsHeaderText(string text) =>
        text is "测试设备" or "设备" or "设备型号" or "测试设备型号" or "测试设备（报表 AO 列）";

    private static string? ResolveWaferSheet(ZipArchive zip)
    {
        var workbook = zip.GetEntry("xl/workbook.xml");
        var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbook is null || rels is null) return null;

        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace officeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        using var wbStream = workbook.Open();
        using var relStream = rels.Open();
        var wbDoc = XDocument.Load(wbStream);
        var relDoc = XDocument.Load(relStream);

        var sheet = wbDoc.Root?
            .Element(ns + "sheets")?
            .Elements(ns + "sheet")
            .FirstOrDefault(x => ((string?)x.Attribute("name"))?
                .Contains("Wafer performance test", StringComparison.OrdinalIgnoreCase) == true);
        if (sheet is null) return null;

        var relId = (string?)sheet.Attribute(officeRel + "id");
        var target = relDoc.Root?
            .Elements(relNs + "Relationship")
            .FirstOrDefault(x => string.Equals((string?)x.Attribute("Id"), relId, StringComparison.OrdinalIgnoreCase))?
            .Attribute("Target")?.Value;
        if (string.IsNullOrWhiteSpace(target)) return null;

        var normalized = target.Replace('\\', '/').TrimStart('/');
        if (!normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
            normalized = "xl/" + normalized;
        return zip.GetEntry(normalized) is null ? null : normalized;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var result = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return result;

        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var si in doc.Root?.Elements(ns + "si") ?? Enumerable.Empty<XElement>())
            result.Add(string.Concat(si.Descendants(ns + "t").Select(t => (string?)t ?? "")));
        return result;
    }

    private static string ReadCellText(XElement cell, XNamespace ns, IReadOnlyList<string> shared)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "s")
        {
            var raw = (string?)cell.Element(ns + "v");
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                index >= 0 && index < shared.Count)
                return shared[index];
            return "";
        }

        if (type == "inlineStr")
        {
            var isNode = cell.Element(ns + "is");
            return isNode is null ? "" : string.Concat(isNode.Descendants(ns + "t").Select(t => (string?)t ?? ""));
        }

        return (string?)cell.Element(ns + "v") ?? "";
    }
}
