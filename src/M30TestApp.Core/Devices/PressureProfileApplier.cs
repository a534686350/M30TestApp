using System;
using M30TestApp.Core.Common;
using M30TestApp.Core.Config;

namespace M30TestApp.Core.Devices;

/// <summary>一次压力控制器配置应用的结果。</summary>
/// <param name="Applied">是否实际写回了配置并重建设备（false = 内容未变化被去重跳过）。</param>
/// <param name="Key">本次配置的去重键，调用方应保存以供下次比较。</param>
/// <param name="Model">归一化后的型号。</param>
/// <param name="Resource">归一化后的 VISA 资源字符串。</param>
public sealed record PressureProfileApplyResult(
    bool Applied, string Key, string Model, string Resource);

/// <summary>
/// 把手动调试页/快速测试页采集的压力控制器型号 + GPIB 参数写回工位配置并重建设备。
/// 此前 Manual 与 QuickTest 两个 ViewModel 各持有一份逐字相同的实现。
/// </summary>
public static class PressureProfileApplier
{
    public static PressureProfileApplyResult Apply(
        TestSession session,
        string? lastAppliedKey,
        string? modelInput,
        string? portInput,
        string? addressInput)
    {
        var existing = session.Station.Get(DeviceKind.Pressure);
        var model = string.IsNullOrWhiteSpace(modelInput) ? existing?.Model ?? "FLUKE-7250" : modelInput!.Trim();
        var port = string.IsNullOrWhiteSpace(portInput) ? "0" : portInput!.Trim();
        var address = string.IsNullOrWhiteSpace(addressInput) ? "0" : addressInput!.Trim();
        var resource = GpibResource.Build(port, address);
        var key = $"{model}|{resource}|{existing?.Backend}|{existing?.Baud}|{existing?.Parity}|{existing?.DataBits}|{existing?.StopBits}";

        if (string.Equals(lastAppliedKey, key, StringComparison.Ordinal))
            return new PressureProfileApplyResult(false, key, model, resource);

        session.Station.Devices[DeviceKind.Pressure] = new DeviceProfile
        {
            Kind = DeviceKind.Pressure,
            Model = model,
            Backend = existing?.Backend ?? DeviceBackend.Hw,
            Address = resource,
            Baud = existing?.Baud ?? 9600,
            Parity = existing?.Parity ?? "N",
            DataBits = existing?.DataBits ?? 8,
            StopBits = existing?.StopBits ?? "1"
        };
        session.RebuildDevices(session.DebugMode);

        return new PressureProfileApplyResult(true, key, model, resource);
    }
}
