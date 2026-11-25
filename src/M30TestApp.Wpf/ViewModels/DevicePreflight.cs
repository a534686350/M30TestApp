using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using M30TestApp.Wpf.Mvvm;

namespace M30TestApp.Wpf.ViewModels;

/// <summary>设备预检的一项：名称、状态、说明。</summary>
public sealed class PreflightItemVm : ViewModelBase
{
    public required string Name { get; init; }

    private string _state = "等待";
    public string State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    private string _detail = "";
    public string Detail
    {
        get => _detail;
        set => SetField(ref _detail, value);
    }

    private bool _isOk;
    public bool IsOk
    {
        get => _isOk;
        set => SetField(ref _isOk, value);
    }

    private bool _isFailed;
    public bool IsFailed
    {
        get => _isFailed;
        set => SetField(ref _isFailed, value);
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set => SetField(ref _isRunning, value);
    }
}

/// <summary>
/// 本轮设备连通性检测的执行器：按采集模式列出需要的设备，逐个 Open + 判状态。
/// 每项带超时，避免 GPIB 无响应时预检卡死。
/// </summary>
public static class DevicePreflight
{
    /// <summary>单台设备打开超时（毫秒）。</summary>
    public const int OpenTimeoutMs = 6000;

    /// <summary>预检结果：是否有失败项 + 失败项描述。</summary>
    public sealed record Result(bool AllOk, IReadOnlyList<string> Failures);

    /// <summary>
    /// 打开设备并等状态；超时/异常都算失败。
    /// 返回 null 表示正常，否则返回失败原因。
    /// </summary>
    public static async Task<string?> ProbeAsync(
        string name, M30TestApp.Core.Devices.IDevice device, CancellationToken ct)
    {
        if (device is null) return "未配置";

        try
        {
            var openTask = device.OpenAsync(ct);
            var finished = await Task.WhenAny(openTask, Task.Delay(OpenTimeoutMs, ct)).ConfigureAwait(false);
            if (finished != openTask)
                return $"打开超时（>{OpenTimeoutMs / 1000}s，检查电源/接线/地址）";

            await openTask.ConfigureAwait(false);
            if (device.State != M30TestApp.Core.Devices.ConnectionState.Connected)
                return $"连接失败（{device.Address}）";

            return null;
        }
        catch (System.Exception ex)
        {
            return ex.Message;
        }
    }
}
