using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using M30TestApp.Core.Common;

namespace M30TestApp.Core.Config;

/// <summary>
/// 方案目录的扁平索引 —— 把过去的「方案文件夹 → 传感器型号」两级选择合并成单一「方案」列表。
///
/// 物理文件不放动：仍留在 <c>setting/TestConfig/&lt;文件夹&gt;/&lt;型号&gt;.ini</c>，
/// 也兼容直接放在 TestConfig 根目录下的 ini。界面只展示一个下拉，选中一项即可拿到完整路径。
/// </summary>
public sealed record PlanEntry(string Name, string Folder, string Path)
{
    /// <summary>下拉显示名。子目录方案显示为「文件夹/型号」，根目录方案只显示型号。</summary>
    public string Display => string.IsNullOrWhiteSpace(Folder) || Folder == "."
        ? Name
        : $"{Folder}/{Name}";
}

public static class PlanCatalog
{
    /// <summary>扫描所有可用方案（根目录 + 各子目录的 *.ini），按显示名排序。</summary>
    public static IReadOnlyList<PlanEntry> List()
    {
        if (!Directory.Exists(AppPaths.TestConfigDir))
            return Array.Empty<PlanEntry>();

        var result = new List<PlanEntry>();

        foreach (var file in Directory.GetFiles(AppPaths.TestConfigDir, "*.ini", SearchOption.TopDirectoryOnly))
            result.Add(new PlanEntry(Path.GetFileNameWithoutExtension(file), "", file));

        foreach (var dir in Directory.GetDirectories(AppPaths.TestConfigDir)
                     .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            foreach (var file in Directory.GetFiles(dir, "*.ini", SearchOption.TopDirectoryOnly)
                         .OrderBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase))
                result.Add(new PlanEntry(Path.GetFileNameWithoutExtension(file), Path.GetFileName(dir), file));

        return result
            .OrderBy(e => e.Display, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>按当前方案的 Name + FolderName 找到匹配项（优先同文件夹，再按名字找）。</summary>
    public static PlanEntry? Find(TestPlan? plan)
    {
        if (plan is null) return null;
        var all = List();
        return all.FirstOrDefault(e => e.Name == plan.Name &&
                                      e.Folder == plan.FolderName)
               ?? all.FirstOrDefault(e => e.Name == plan.Name);
    }

    /// <summary>按显示名（下拉 SelectedItem）找到条目。</summary>
    public static PlanEntry? FindByDisplay(string? display, IEnumerable<PlanEntry> entries) =>
        string.IsNullOrWhiteSpace(display)
            ? null
            : entries.FirstOrDefault(e => e.Display == display);
}
