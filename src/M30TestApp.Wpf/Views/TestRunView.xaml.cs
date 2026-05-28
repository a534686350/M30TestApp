using System.Windows.Controls;

namespace M30TestApp.Wpf.Views;

public partial class TestRunView : UserControl
{
    public TestRunView()
    {
        InitializeComponent();
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        LogTextBox.CaretIndex = LogTextBox.Text.Length;
        LogTextBox.ScrollToEnd();
    }
}
