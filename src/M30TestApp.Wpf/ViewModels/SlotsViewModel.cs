using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

/// <summary>
/// 顶部「工位」页签的页面 VM —— 面向**不需要扫码**的场景：
/// 只要输入工位数量、看工位表即可。
///
/// 它本身不持有任何数据，只是把 <see cref="ConfigViewModel"/> 里的工位表与布局参数
/// 换个更简单的形态呈现。**绝不能在这里再建一份工位表** —— 工位表必须只有一份，
/// 自动测试 / 长期稳定性 / 快速测试 / 配置页共用同一份，否则现场会出现
/// 「工位页改了数量、跑测试还是旧的」这类对不上的问题。
///
/// XAML 里统一经 <c>{Binding Config.*}</c> 绑定，WPF 会自动订阅 ConfigViewModel 的
/// 属性通知（<c>Config.PreviewCount</c> 这类路径是有效的）。
/// </summary>
public sealed class SlotsViewModel : ViewModelBase
{
    public SlotsViewModel(ConfigViewModel config) => Config = config;

    /// <summary>真正持有工位数据与命令的 VM（本页所有绑定都经它）。</summary>
    public ConfigViewModel Config { get; }
}
