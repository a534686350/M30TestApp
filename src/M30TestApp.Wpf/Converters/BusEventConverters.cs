using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using M30TestApp.Core.Common;

namespace M30TestApp.Wpf.Converters;

/// <summary>Converts a <see cref="BusDirection"/> into a foreground brush for the trace list.</summary>
public sealed class BusDirectionToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not BusDirection d) return DependencyProperty.UnsetValue;
        var key = d switch
        {
            BusDirection.Tx => "AccentBrush",
            BusDirection.Rx => "SuccessBrush",
            _ => "MutedBrush",
        };
        return Application.Current.Resources[key] as Brush ?? Brushes.Black;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
