using System;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;
using M30TestApp.Core.Devices.Hw;
using M30TestApp.Core.Devices.Sim;

namespace M30TestApp.Core.Devices;

/// <summary>
/// Builds devices from a StationProfile. Each device kind has a SIM backend and
/// (eventually) HW backends keyed by the model name in Command.ini.
///
/// For now, HW backends fall back to SIM with a warning so the rest of the system
/// can run end-to-end. Real SCPI/serial backends will be wired in as we migrate the
/// original Func_*.cs drivers.
/// </summary>
public sealed class DeviceFactory
{
    private readonly CommandDictionary _commands;
    private readonly bool _forceSim;

    public DeviceFactory(CommandDictionary commands, bool forceSim = false)
    {
        _commands = commands;
        _forceSim = forceSim;
    }

    private bool UseHw(DeviceProfile p) => !_forceSim && p.Backend == DeviceBackend.Hw;

    public IPressureController CreatePressure(DeviceProfile p)
        => UseHw(p)
            ? new HwPressureController(p, _commands)
            : new SimPressureController(p.Model, p.Address, _commands);

    public IOven CreateOven(DeviceProfile p)
        => UseHw(p)
            ? new HwOven(p)
            : new SimOven(p.Model, p.Address, _commands);

    public IDmm CreateDmm(DeviceProfile p)
        => UseHw(p)
            ? new HwDmm(p, _commands)
            : new SimDmm(p.Model, p.Address, _commands);

    public IDac CreateDac(DeviceProfile p)
        => UseHw(p)
            ? new HwDac(p)
            : new SimDac(p.Model, p.Address, _commands);

    public IPowerSupply CreatePower(DeviceProfile p)
        => UseHw(p)
            ? new HwPowerSupply(p, _commands)
            : new SimPower(p.Model, p.Address, _commands);

    public IBoard CreateBoard(DeviceProfile p)
        => UseHw(p)
            ? new HwBoard(p)
            : new SimBoard(p.Model, p.Address, _commands);

    /// <summary>
    /// 现场没接设备也能把流程跑起来：**打开失败就当场换成同类型的 SIM 实例**。
    ///
    /// 常见场景：仪器还没上电、线没插、串口被别的程序占着、CP2102 驱动没装。
    /// 这时候整条测试链路（方案 → 工位 → 保温 → 控压 → 采集 → 判定 → 导出）都应该能跑通，
    /// 只是数据是模拟的；真机接好之后重新开一次程序即可。
    ///
    /// 打开成功就保持打开状态并原样返回 —— 上层靠 <see cref="IDevice.State"/> 判断
    /// 能不能用，换成 SIM 后 State 是 Connected，不会被打断。
    /// </summary>
    /// <param name="hw">待探测的设备（已经是 SIM 就直接返回）。</param>
    /// <param name="openTimeout">单台探测超时；VISA/串口打不开时常常要等，不能无限等。</param>
    public IDevice FallbackIfOffline(IDevice hw, int openTimeoutSeconds = 8)
    {
        // SIM 设备没有硬件依赖，不需要探测
        if (hw.GetType().Namespace?.Contains(".Hw.", StringComparison.Ordinal) != true)
            return hw;

        try
        {
            var task = System.Threading.Tasks.Task.Run(() => hw.OpenAsync());
            var ok = task.Wait(TimeSpan.FromSeconds(openTimeoutSeconds))
                     && hw.State == ConnectionState.Connected;
            if (ok)
            {
                AppLog.Info("Sim", $"{hw.Kind} {hw.Model}@{hw.Address} 连上了，用真机");
                return hw;
            }
            AppLog.Warn("Sim", $"{hw.Kind} {hw.Model}@{hw.Address} 打不开，改用模拟");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Sim", $"{hw.Kind} {hw.Model}@{hw.Address} 打开异常，改用模拟：{ex.Message}");
        }

        try { hw.Dispose(); } catch { /* 已经打不开的设备，失败了也无所谓 */ }

        IDevice sim = hw.Kind switch
        {
            DeviceKind.Pressure => new SimPressureController(hw.Model, hw.Address, _commands),
            DeviceKind.Oven => new SimOven(hw.Model, hw.Address, _commands),
            DeviceKind.Dmm => new SimDmm(hw.Model, hw.Address, _commands),
            DeviceKind.Dac => new SimDac(hw.Model, hw.Address, _commands),
            DeviceKind.Power => new SimPower(hw.Model, hw.Address, _commands),
            DeviceKind.Board => new SimBoard(hw.Model, hw.Address, _commands),
            _ => hw,
        };

        if (ReferenceEquals(sim, hw)) return hw;

        AppLog.Info("Session", $"{hw.Kind} {hw.Model}@{hw.Address} → SIM（{sim.Model}）");
        _ = sim.OpenAsync();   // SIM 的打开永远成功，只是为了让 State = Connected
        return sim;
    }
}
