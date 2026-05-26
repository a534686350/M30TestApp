using System.Windows;
using System.Windows.Input;

namespace M30TestApp.Wpf.Views;

public partial class SlotCountDialog : Window
{
    public SlotCountDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => { CountBox.Focus(); CountBox.SelectAll(); };
    }

    public int Count
    {
        get => int.TryParse(CountBox.Text, out var v) ? v : 1;
        set => CountBox.Text = value.ToString();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CountBox.Text, out _))
        {
            MessageBox.Show("请输入整数。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void OnCountKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnOk(sender, e);
            e.Handled = true;
        }
    }
}
