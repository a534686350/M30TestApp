using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using M30TestApp.Core.Common;
using M30TestApp.Core.TaskScript;

namespace M30TestApp.Core.Data;

/// <summary>
/// Persists run progress for <c>Run:PerformanceTest</c> so a cancelled or crashed test can resume.
/// </summary>
public sealed class TestCheckpoint
{
    public string PlanName { get; set; } = "";
    public int TempIndex { get; set; }
    /// <summary>Next pressure index within <see cref="TempIndex"/>; 0 if still in pre-pressure phase.</summary>
    public int PressureIndex { get; set; }
    public bool LeakCheckDone { get; set; }
    public DateTime SavedAt { get; set; }
    public List<string> Columns { get; set; } = new();

    public static string JsonPath => Path.Combine(AppPaths.DataDir, "checkpoint.json");
    public static string MatrixPath => Path.Combine(AppPaths.DataDir, "checkpoint_matrix.csv");

    public static bool Exists() => File.Exists(JsonPath);

    public static TestCheckpoint? Load()
    {
        if (!File.Exists(JsonPath)) return null;
        try
        {
            var json = File.ReadAllText(JsonPath);
            return JsonSerializer.Deserialize<TestCheckpoint>(json);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Checkpoint", $"无法读取断点: {ex.Message}");
            return null;
        }
    }

    public static void Save(TaskContext ctx, int tempIndex, int pressureIndex, bool leakCheckDone)
    {
        if (!AppPreferences.SaveCheckpointOnAbort(ctx.Settings)) return;

        var ck = new TestCheckpoint
        {
            PlanName = ctx.Plan.Name,
            TempIndex = tempIndex,
            PressureIndex = pressureIndex,
            LeakCheckDone = leakCheckDone,
            SavedAt = DateTime.Now,
            Columns = ctx.Columns.ToList(),
        };

        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(JsonPath, JsonSerializer.Serialize(ck, new JsonSerializerOptions { WriteIndented = true }));

        var serialMap = ctx.Slots.Entries.ToDictionary(s => s.Slot, s => s.SerialNo);
        ctx.Matrix.ExportCsv(MatrixPath, ctx.Columns, serialMap);
        AppLog.Info("Checkpoint", $"已保存断点：{ck.PlanName} T{tempIndex} P{pressureIndex}");
    }

    public static void RestoreMatrix(TaskContext ctx, TestCheckpoint ck)
    {
        if (!File.Exists(MatrixPath)) return;
        ctx.Matrix.ImportCsv(MatrixPath, out var columns);
        ctx.Columns.Clear();
        foreach (var c in columns) ctx.Columns.Add(c);
        if (ck.Columns.Count > 0 && ctx.Columns.Count == 0)
        {
            foreach (var c in ck.Columns) ctx.Columns.Add(c);
        }
        foreach (var slot in ctx.Slots.Entries) ctx.Matrix.EnsureSlot(slot.Slot);
        AppLog.Info("Checkpoint", $"已恢复矩阵 {ctx.Columns.Count} 列");
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(JsonPath)) File.Delete(JsonPath);
            if (File.Exists(MatrixPath)) File.Delete(MatrixPath);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Checkpoint", $"清除断点失败: {ex.Message}");
        }
    }

    public bool MatchesPlan(string planName) =>
        !string.IsNullOrWhiteSpace(PlanName) &&
        string.Equals(PlanName, planName, StringComparison.OrdinalIgnoreCase);
}
