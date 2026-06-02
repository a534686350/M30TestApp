using System.Windows.Controls;

namespace M30TestApp.Wpf.Views;

public partial class QuickTestView : UserControl
{
    public QuickTestView()
    {
        InitializeComponent();
    }

    private void QuickLogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        QuickLogTextBox.CaretIndex = QuickLogTextBox.Text.Length;
        QuickLogTextBox.ScrollToEnd();
    }
}
