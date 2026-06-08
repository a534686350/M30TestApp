using System.Windows;

namespace M30TestApp.Wpf.Views;

public partial class BulkPointEditorWindow : Window
{
    public BulkPointEditorWindow(string title, string text)
        : this(title, text, "")
    {
    }

    public BulkPointEditorWindow(string title, string text, string hint)
    {
        InitializeComponent();
        Title = title;
        if (!string.IsNullOrWhiteSpace(hint))
            HintTextBlock.Text = hint;

        InputTextBox.Text = text;
        InputTextBox.SelectAll();
        InputTextBox.Focus();
    }

    public string Text => InputTextBox.Text;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
