using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using M30TestApp.Core.Config;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

// ConfigViewModel 的方案部分（partial）：方案列表、压力/温度点、探漏设置显示逻辑。
public sealed partial class ConfigViewModel : ViewModelBase
{
    private TestPlan _plan = new();
    public TestPlan Plan { get => _plan; private set => SetField(ref _plan, value); }
    public string TaskScript => Plan.TaskScript;
    public ObservableCollection<string> PlanFolders { get; } = new();
    public ObservableCollection<string> SensorModelFiles { get; } = new();
    private string _selectedPlanFolder = "";
    private string _selectedSensorModelFile = "";
    private string _loadedPlanFolder = "";
    private string _loadedSensorModelFile = "";
    private bool _loadingPlan;
    public string SelectedPlanFolder
    {
        get => _selectedPlanFolder;
        set
        {
            if (!SetField(ref _selectedPlanFolder, value)) return;
            RefreshSensorModelFiles();
        }
    }
    public string SelectedSensorModelFile
    {
        get => _selectedSensorModelFile;
        set
        {
            if (!SetField(ref _selectedSensorModelFile, value)) return;
            if (!_loadingPlan && !string.IsNullOrWhiteSpace(value)) LoadPlanByFile(value);
        }
    }
    public ObservableCollection<PressurePoint> PressurePoints { get; } = new();
    public ObservableCollection<TempPoint> TempPoints { get; } = new();

    /// <summary>方案默认压力类型的中文显示名，供 ComboBox 双向绑定。</summary>
    public string PlanDefaultPressureTypeDisplay
    {
        get => PointBatchParser.PressureTypeToDisplay(Plan.DefaultPressureType);
        set
        {
            var previous = Plan.DefaultPressureType;
            var next = PointBatchParser.ResolvePressureType(value, Core.Config.PressureType.Gauge);
            if (previous == next) return;

            Plan.DefaultPressureType = next;
            foreach (var pp in PressurePoints.Where(p => p.PressureType == previous))
                pp.PressureType = next;
            RefreshPressurePointRows();
            OnPropertyChanged();
            OnPropertyChanged(nameof(LeakCheckPressuresHint));
        }
    }

    public string LeakCheckPressuresText
    {
        get => Plan.LeakCheck.Pressures.Count == 0
            ? ""
            : string.Join(", ", Plan.LeakCheck.Pressures.Select(p => p.ToString(CultureInfo.InvariantCulture)));
        set
        {
            Plan.LeakCheck.Pressures.Clear();
            foreach (var part in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var pressure))
                    Plan.LeakCheck.Pressures.Add(pressure);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(LeakCheckPressuresHint));
        }
    }

    public string LeakCheckPrecisionText
    {
        get => Plan.LeakCheck.Precision?.ToString(CultureInfo.InvariantCulture) ?? "";
        set
        {
            Plan.LeakCheck.Precision = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var precision)
                ? precision
                : null;
            OnPropertyChanged();
        }
    }

    public bool UseDefaultLeakCheckPrecision
    {
        get => !Plan.LeakCheck.Precision.HasValue;
        set
        {
            if (value)
                Plan.LeakCheck.Precision = null;
            else if (!Plan.LeakCheck.Precision.HasValue)
                Plan.LeakCheck.Precision = Plan.Precision;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LeakCheckPrecisionText));
            OnPropertyChanged(nameof(LeakCheckPrecisionDisplay));
        }
    }

    public string LeakCheckPrecisionDisplay => UseDefaultLeakCheckPrecision
        ? $"当前型号默认漏率指标：{FormatLeakRate(LeakCheckPlanHelper.ResolveDefaultLeakRateLimit(Plan), Plan.PressureUnit)}（表压/差压=满量程/20000）"
        : $"手动漏率指标：{FormatLeakRate(Plan.LeakCheck.Precision ?? 0f, Plan.PressureUnit)}";

    private static string FormatLeakRate(float value, string unit)
    {
        unit = string.IsNullOrWhiteSpace(unit) ? "kPa" : unit.Trim();
        var main = value.ToString("G6", CultureInfo.InvariantCulture) + unit + "/s";
        var paPerSec = unit.Equals("kPa", StringComparison.OrdinalIgnoreCase)
            ? value * 1000f
            : unit.Equals("MPa", StringComparison.OrdinalIgnoreCase)
                ? value * 1000000f
                : unit.Equals("Pa", StringComparison.OrdinalIgnoreCase) ? value : float.NaN;
        return float.IsNaN(paPerSec)
            ? main
            : $"{main}（{paPerSec.ToString("G6", CultureInfo.InvariantCulture)}Pa/s）";
    }

    public string LeakCheckPressuresHint
    {
        get
        {
            var fullScale = LeakCheckPlanHelper.ResolveFullScale(Plan);
            var unit = string.IsNullOrWhiteSpace(Plan.PressureUnit) ? "kPa" : Plan.PressureUnit;
            var desc = LeakCheckPlanHelper.DescribeDefaultPressures(Plan);
            return fullScale > 0 ? $"{desc}（满量程 {fullScale}{unit}）" : desc;
        }
    }
}
