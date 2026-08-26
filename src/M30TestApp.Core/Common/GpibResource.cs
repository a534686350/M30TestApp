using System;

namespace M30TestApp.Core.Common;

/// <summary>
/// GPIB 资源地址（<c>GPIB{port}::{address}::INSTR</c>）的解析与构建。
/// 此前该逻辑在 Config/Manual/QuickTest 三个 ViewModel 各复制一份，行为已出现细微分叉。
/// </summary>
public static class GpibResource
{
    /// <summary>从 VISA 资源字符串解析板卡号与地址；无法解析时返回 ("0","0")。</summary>
    public static (string Port, string Address) Parse(string? resource)
    {
        var port = "0";
        var address = "0";
        if (string.IsNullOrWhiteSpace(resource)) return (port, address);
        if (!resource.StartsWith("GPIB", StringComparison.OrdinalIgnoreCase)) return (port, address);

        var parts = resource.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return (port, address);

        port = parts[0].Length > 4 ? parts[0][4..] : "0";
        address = parts[1];
        return (port, address);
    }

    public static string Build(string port, string address) => $"GPIB{port}::{address}::INSTR";
}
