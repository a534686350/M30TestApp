using System;
using System.Windows;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

/// <summary>
/// 测试任务独立窗口：点左侧导航的自动/长期/快速/手动调试时打开，一次只有一个。
/// 关掉窗口回主界面。运行中主界面所有导航与配置入口都禁用（由 MainViewModel.IsTaskRunning 控制）。
///
/// DataContext = MainViewModel（顶栏开始/停止命令），TaskHost.Content = 具体任务 VM
/// （App.xaml 的 DataTemplate 渲染成对应 View）。两者不同，不要搞混。
/// </summary>
public sealed partial class TaskWindow : Window
{
    private static TaskWindow? s_active;
    private readonly Action _onClosed;
    private readonly string _key;

    private TaskWindow(string key, string title, MainViewModel shell, object taskVm, Action onClosed)
    {
        InitializeComponent();
        _key = key;
        _onClosed = onClosed;
        DataContext = shell;              // 顶栏开始/停止
        Title = title;
        TitleText.Text = title;
        TaskHost.Content = taskVm;        // 任务页内容
        Owner = Application.Current.MainWindow;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(s_active, this)) s_active = null;

            // 关掉任务窗 → 恢复并显示主界面（开窗时把它 Hide 了）。
            // Hide 不卸载窗口，IsLoaded 仍为 true；主窗若被退出流程关闭则 IsLoaded=false，不复活它。
            var main = Owner;
            if (main is not null && main.IsLoaded)
            {
                try
                {
                    main.Show();
                    main.Activate();
                }
                catch (InvalidOperationException)
                {
                    // 程序正在退出（Application.Shutdown 关任务窗 → 连锁关主窗），
                    // 此时 Show 会抛「窗口关闭期间无法设置可见性」。忽略即可，本来就要退出了。
                }
            }
            _onClosed();
        };
    }

    /// <summary>开任务窗；已有其它任务窗时拒绝（一次只能一个任务）。返回是否成功打开。</summary>
    public static bool Open(string key, string title, MainViewModel shell, object taskVm, Action onClosed)
    {
        if (s_active is not null)
        {
            if (s_active._key == key)
            {
                s_active.Activate();
                return true;
            }
            return false;   // 已有别的任务窗，禁止再开
        }

        // 隐藏主界面：测试期间主窗即使有 CanExecute 锁也不该露出来让人误操作。
        // 关窗时 Closed 事件里 Show + Activate 恢复。
        Application.Current.MainWindow?.Hide();

        var window = new TaskWindow(key, title, shell, taskVm, onClosed);
        s_active = window;
        window.Show();
        return true;
    }

    /// <summary>任务窗口是否开着。</summary>
    public static bool IsOpen => s_active is not null;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
