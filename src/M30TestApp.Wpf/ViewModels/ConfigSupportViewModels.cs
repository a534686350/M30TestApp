using System.Collections.ObjectModel;
using M30TestApp.Core.Config;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

// ────────────────────────────────────────────────────────────────────────────
// 配置中心使用的小型行/条目 ViewModel 集合。
// 从 ConfigViewModel.cs 抽出独立维护；XAML 按命名空间解析类型，与所在文件无关。
// ────────────────────────────────────────────────────────────────────────────

/// <summary>指标开关行：Enabled 绑定开关，Min/Max 直接读写绑定的 SpecRange。</summary>
public sealed class MetricSwitch : ViewModelBase
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    private bool _enabled;
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }

    private SpecRange? _spec;
    public string Min
    {
        get => _spec?.Min ?? "";
        set
        {
            if (_spec is null || _spec.Min == value) return;
            _spec.Min = value;
            OnPropertyChanged();
        }
    }

    public string Max
    {
        get => _spec?.Max ?? "";
        set
        {
            if (_spec is null || _spec.Max == value) return;
            _spec.Max = value;
            OnPropertyChanged();
        }
    }

    public void BindSpec(SpecRange spec)
    {
        _spec = spec;
        OnPropertyChanged(nameof(Min));
        OnPropertyChanged(nameof(Max));
    }
}

/// <summary>指令模板行（动作名 + SCPI 模板）。</summary>
public sealed class CommandTemplateVm : ViewModelBase
{
    public string Action { get; init; } = "";

    private string _template = "";
    public string Template { get => _template; set => SetField(ref _template, value); }
}

/// <summary>指令页的「设备 · 型号」分组行。</summary>
public sealed class ModelCommandsVm
{
    public string Kind { get; init; } = "";
    public string Model { get; init; } = "";
    public string DisplayName => $"{Kind} · {Model}";
    public ObservableCollection<CommandTemplateVm> Templates { get; } = new();
}

/// <summary>测试流程步骤行。</summary>
public sealed class TaskStepVm : ViewModelBase
{
    private int _index;
    public int Index { get => _index; set => SetField(ref _index, value); }

    public string Text { get; init; } = "";
    public string Module => Text.Split(':') is { Length: >= 1 } parts ? parts[0] : "";
}

/// <summary>通用「名称=值」设置行，可选下拉选项与 ini 落盘定位。</summary>
public sealed class SettingPairVm : ViewModelBase
{
    public SettingPairVm(string name, string value, IEnumerable<string>? options = null, string unit = "", string section = "", string key = "")
    {
        Name = name;
        _value = value;
        Unit = unit;
        Section = section;
        Key = string.IsNullOrWhiteSpace(key) ? name : key;
        Options = options is null ? new ObservableCollection<string>() : new ObservableCollection<string>(options);
    }

    public string Name { get; }
    public string Unit { get; }
    public string Section { get; }
    public string Key { get; }
    public ObservableCollection<string> Options { get; }
    public bool HasOptions => Options.Count > 0;

    private string _value;
    public string Value { get => _value; set => SetField(ref _value, value); }
}

/// <summary>压力控制器指令编辑行。</summary>
public sealed class PressureCommandSettingVm : ViewModelBase
{
    public PressureCommandSettingVm(string name, string command)
    {
        Name = name;
        _command = command;
    }

    public string Name { get; }
    private string _command;
    public string Command { get => _command; set => SetField(ref _command, value); }
}
