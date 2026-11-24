using System;
using System.Linq;
using System.Windows;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

/// <summary>
/// 选择运行方案窗口。
///
/// 2026-11-24 起只保留「选择方案」一页：工位统一在侧边栏「工位表」配置并保存，
/// 运行时直接用那份，不再在这里重建工位表（原来两张工位表互相覆盖）。
/// 因此原先工位页签的扫码录入 / 勾选格拖动 / 预览表相关 code-behind 全部移除。
/// </summary>
public partial class RunSetupWindow : Window
{
    public RunSetupWindow(RunSetupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += (_, _) =>
        {
            DialogResult = vm.DialogResult;
            Close();
        };
    }

    /// <summary>把输入框里的型号加进下拉候选（只影响本次运行的候选列表，不改任何配置文件）。</summary>
    private void OnAddReportDevice(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RunSetupViewModel vm) return;

        var name = (vm.ReportDevice ?? "").Trim();
        if (name.Length == 0) return;
        if (vm.ReportDeviceOptions.Any(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase))) return;

        vm.ReportDeviceOptions.Add(name);
        vm.ReportDevice = name;
    }
}
