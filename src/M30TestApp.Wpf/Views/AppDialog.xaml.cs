using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace M30TestApp.Wpf.Views;

/// <summary>
/// 主题化消息框 —— 系统 MessageBox 的替代品。
///
/// 为什么要有它：系统 MessageBox 是旧版 Windows 外观（直角、系统按钮、白底），
/// 跟这套扁平主题不搭，深色主题下更是突兀。这个窗口用应用自己的主题画刷，
/// 按钮也是应用统一的「主色 / 幽灵」两种。
///
/// **重载与 MessageBox.Show 一致**（含 Window owner 版本、含返回 MessageBoxResult），
/// 所以调用点只需要把 <c>MessageBox.Show</c> 换成 <c>AppDialog.Show</c>，其余代码一行都不用动。
/// </summary>
public partial class AppDialog : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;
    private MessageBoxResult _fallback = MessageBoxResult.OK;

    private AppDialog(Window? explicitOwner)
    {
        InitializeComponent();

        // 挂了 owner 才会居中且跟随主窗口层级；没有主窗口时退到屏幕居中
        var owner = explicitOwner ?? Application.Current?.MainWindow;
        if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, this))
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    // ── 与 MessageBox.Show 对齐的重载 ────────────────────────────────────

    public static MessageBoxResult Show(string messageBoxText)
        => ShowCore(null, messageBoxText, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None, hasDefault: false);

    public static MessageBoxResult Show(string messageBoxText, string caption)
        => ShowCore(null, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None, hasDefault: false);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button)
        => ShowCore(null, messageBoxText, caption, button, MessageBoxImage.None, MessageBoxResult.None, hasDefault: false);

    public static MessageBoxResult Show(string messageBoxText, string caption,
        MessageBoxButton button, MessageBoxImage image)
        => ShowCore(null, messageBoxText, caption, button, image, MessageBoxResult.None, hasDefault: false);

    public static MessageBoxResult Show(string messageBoxText, string caption,
        MessageBoxButton button, MessageBoxImage image, MessageBoxResult defaultResult)
        => ShowCore(null, messageBoxText, caption, button, image, defaultResult, hasDefault: true);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption)
        => ShowCore(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None, hasDefault: false);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button)
        => ShowCore(owner, messageBoxText, caption, button, MessageBoxImage.None, MessageBoxResult.None, hasDefault: false);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption,
        MessageBoxButton button, MessageBoxImage image)
        => ShowCore(owner, messageBoxText, caption, button, image, MessageBoxResult.None, hasDefault: false);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption,
        MessageBoxButton button, MessageBoxImage image, MessageBoxResult defaultResult)
        => ShowCore(owner, messageBoxText, caption, button, image, defaultResult, hasDefault: true);

    /// <summary>统一入口：自绘窗口失败时回退系统 MessageBox，保证提示不丢。</summary>
    private static MessageBoxResult ShowCore(Window? owner, string messageBoxText, string caption,
        MessageBoxButton button, MessageBoxImage image, MessageBoxResult defaultResult, bool hasDefault)
    {
        var app = Application.Current;
        // 非 UI 线程或应用还没起来：直接走系统对话框，别在这里炸
        if (app is null || app.Dispatcher.CheckAccess() == false)
            return MessageBox.Show(messageBoxText, caption, button, image);

        try
        {
            var dlg = new AppDialog(owner);
            dlg.Build(messageBoxText ?? "", caption, button, image);
            dlg._fallback = hasDefault ? defaultResult : DefaultResult(button);
            dlg.ShowDialog();
            return dlg._result == MessageBoxResult.None ? dlg._fallback : dlg._result;
        }
        catch (Exception)
        {
            // 可能正处在保存 / 关闭程序的关键路径上，异常绝不能往调用方抛
            return MessageBox.Show(messageBoxText, caption, button, image);
        }
    }

    private static MessageBoxResult DefaultResult(MessageBoxButton button) => button switch
    {
        MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
        MessageBoxButton.YesNo => MessageBoxResult.No,
        MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
        _ => MessageBoxResult.OK,
    };

    private void Build(string message, string caption, MessageBoxButton button, MessageBoxImage image)
    {
        Title = string.IsNullOrWhiteSpace(caption) ? "M30 测试系统" : caption;
        TitleText.Text = string.IsNullOrWhiteSpace(caption) ? "提示" : caption;
        MessageText.Text = message;

        // 图标点颜色按语义走
        var brushKey = image switch
        {
            MessageBoxImage.Error or MessageBoxImage.Stop or MessageBoxImage.Hand => "ErrorBrush",
            MessageBoxImage.Warning or MessageBoxImage.Exclamation => "WarnBrush",
            _ => "AccentBrush",
        };
        if (TryFindResource(brushKey) is Brush brush) IconDot.Fill = brush;

        // 按钮：主色给「肯定」的那一个，其余幽灵
        switch (button)
        {
            case MessageBoxButton.OK:
                AddButton("确定", MessageBoxResult.OK, primary: true, isDefault: true, isCancel: false);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("取消", MessageBoxResult.Cancel, primary: false, isDefault: false, isCancel: true);
                AddButton("确定", MessageBoxResult.OK, primary: true, isDefault: true, isCancel: false);
                break;
            case MessageBoxButton.YesNo:
                AddButton("否", MessageBoxResult.No, primary: false, isDefault: false, isCancel: true);
                AddButton("是", MessageBoxResult.Yes, primary: true, isDefault: true, isCancel: false);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("取消", MessageBoxResult.Cancel, primary: false, isDefault: false, isCancel: true);
                AddButton("否", MessageBoxResult.No, primary: false, isDefault: false, isCancel: false);
                AddButton("是", MessageBoxResult.Yes, primary: true, isDefault: false, isCancel: false);
                break;
        }

        // 轻微淡入，避免生硬弹出
        Opacity = 0;
        Loaded += (_, _) =>
        {
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110));
            BeginAnimation(OpacityProperty, fade);
        };
    }

    private void AddButton(string text, MessageBoxResult result, bool primary, bool isDefault, bool isCancel)
    {
        var btn = new Button
        {
            Content = text,
            MinWidth = 88,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 12,
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        if (TryFindResource(primary ? "PrimaryButton" : "GhostButton") is Style style) btn.Style = style;

        btn.Click += (_, _) =>
        {
            _result = result;
            Close();
        };
        ButtonBar.Children.Add(btn);
    }
}
