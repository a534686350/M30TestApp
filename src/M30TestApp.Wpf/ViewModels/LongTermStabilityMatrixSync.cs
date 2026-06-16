using System.Collections.ObjectModel;
using M30TestApp.Core.Config;
using M30TestApp.Core.Data;
using M30TestApp.Core.Common;

namespace M30TestApp.Wpf.ViewModels;

/// <summary>
/// Syncs long-term stability matrix columns from a test plan to the UI grid.
/// </summary>
public static class LongTermStabilityMatrixSync
{
    public static void Apply(
        ObservableCollection<LongTermMatrixColumnVm> columns,
        TestPlan plan,
        LongTermStabilityMeasureMode mode = LongTermStabilityMeasureMode.Voltage)
    {
        columns.Clear();
        var groupIndex = 0;
        string? lastTempPoint = null;
        foreach (var col in LongTermStabilityMatrix.BuildColumns(plan, mode))
        {
            if (col.TempPointName != lastTempPoint)
            {
                groupIndex++;
                lastTempPoint = col.TempPointName;
            }
            columns.Add(LongTermMatrixColumnVm.From(col, groupIndex));
        }
        var dmmLabel = mode == LongTermStabilityMeasureMode.Resistance ? "电阻" : "电压";
        AppLog.Info("UI", $"长期稳定性表格（{dmmLabel}）：{columns.Count} 列（{plan.TempPoints.Count} 温度点 × ({plan.PressurePoints.Count} 压力 + 1 Temp)）");
    }
}
