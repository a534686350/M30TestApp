using System;
using System.Globalization;
using System.Windows.Data;

namespace M30TestApp.Wpf.Converters;

/// <summary>
/// 把「MainViewModel.SelectedNavKey == 参数」双向映射成 bool，
/// 用于顶部模块页签：IsChecked="{Binding SelectedNavKey, Converter={StaticResource NavKeyIs}, ConverterParameter=TestRun}"。
/// ConvertBack 只在被选中时回写键名；取消选中返回 DoNothing，避免点另一个页签时把选中态清空。
/// </summary>
public sealed class NavKeyIsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string key) return key;
        return Binding.DoNothing;
    }
}
