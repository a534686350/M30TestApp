using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace M30TestApp.Wpf.Views;

/// <summary>通用文本输入对话框（新增型号、重命名等场景复用）。</summary>
public partial class TextInputDialog : Window
{
    private TextInputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    /// <summary>弹出一个输入框；用户取消或未输入有效内容时返回 null。</summary>
    public static string? Prompt(string title, string prompt, string initial = "", string hint = "")
    {
        var dlg = new TextInputDialog
        {
            Title = title,
            Owner = Application.Current?.MainWindow,
        };
        dlg.PromptText.Text = prompt;
        dlg.InputBox.Text = initial;
        dlg.HintText.Text = hint;
        dlg.HintText.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        return dlg.ShowDialog() == true ? dlg.InputBox.Text.Trim() : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0)
        {
            MessageBox.Show("名称不能为空。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (text.Any(c => c is '[' or ']' or '=' or ';'))
        {
            MessageBox.Show("名称不能包含 [ ] = ; 这些字符。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnOk(sender, e);
        e.Handled = true;
    }
}
