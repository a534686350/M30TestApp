using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ivi.Visa.Interop;
using M30TestApp.Core.Common;
using M30TestApp.Core.Devices.Hw;

namespace M30TestApp.Core.Config;

/// <summary>
/// GPIB / USB VISA 仪器扫描 —— 自动发现总线上的 DAQ973A，避免现场手填地址。
///
/// 底层用 NI-VISA 的 <c>ResourceManager.FindRsrc</c> 枚举资源
/// （<c>GPIB?*INSTR</c> + <c>USB?*INSTR</c>），再逐个发 <c>*IDN?</c> 识别型号
/// （含 "973A" 即认为是目标万用表）。
///
/// 现场既有 GPIB 接法（<c>GPIB0::12::INSTR</c>）也有 USB 接法
/// （<c>USB0::0x2A8D::0x8601::MY59018500::0::INSTR</c>），两种都要能扫到。
///
/// NI-VISA 未安装 / 驱动异常时静默返回空列表 —— 现场可继续手填地址，不能被扫描拖垮。
/// </summary>
public static class VisaScanner
{
    private static readonly Regex GpibAddressPattern =
        new(@"^GPIB\d+::\d+::INSTR$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>USB INSTR 资源：<c>USB0::0x2A8D::0x8601::MY59018500::0::INSTR</c>。</summary>
    private static readonly Regex UsbAddressPattern =
        new(@"^USB\d+::.+::INSTR$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>扫描所有 GPIB INSTR 资源（不做型号识别）。</summary>
    public static IReadOnlyList<string> FindGpibResources()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in EnumerateResources("GPIB?*INSTR"))
        {
            if (GpibAddressPattern.IsMatch(s)) found.Add(s);
        }
        return found.ToArray();
    }

    /// <summary>扫描所有 USB INSTR 资源（不做型号识别）。</summary>
    public static IReadOnlyList<string> FindUsbResources()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in EnumerateResources("USB?*INSTR"))
        {
            if (UsbAddressPattern.IsMatch(s)) found.Add(s);
        }
        return found.ToArray();
    }

    /// <summary>扫描全部 VISA INSTR 资源（GPIB + USB，不做型号识别）。</summary>
    public static IReadOnlyList<string> FindVisaResources()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in FindGpibResources()) found.Add(s);
        foreach (var s in FindUsbResources()) found.Add(s);
        return found.ToArray();
    }

    /// <summary>按 NI-VISA 的表达式枚举资源；COM 未注册 / 位数不匹配时返回空。</summary>
    private static IEnumerable<string> EnumerateResources(string pattern)
    {
        ResourceManager rm;
        try
        {
            rm = new ResourceManager();
        }
        catch
        {
            // NI-VISA COM 未注册 / 位数不匹配（32/64）—— 静默回退到手填。
            return Array.Empty<string>();
        }

        try
        {
            var resources = rm.FindRsrc(pattern);
            if (resources is not Array arr) return Array.Empty<string>();

            var list = new List<string>();
            foreach (var item in arr)
            {
                var s = Convert.ToString(item)?.Trim();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
            return list;
        }
        catch (Exception ex)
        {
            AppLog.Warn("GpibScan", $"枚举 {pattern} 资源失败：{ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 扫描并识别 DAQ973A。对每个 GPIB / USB 资源发 *IDN?，保留型号串含 "973A" 的地址。
    /// 识别失败（设备不开机 / 非 973）不影响其它资源。
    /// </summary>
    public static IReadOnlyList<string> FindDmm973()
    {
        var result = new List<string>();
        foreach (var address in FindVisaResources())
        {
            try
            {
                using var visa = new VisaSession("GpibScan", address);
                var idn = visa.QueryStringWithTimeout("*IDN?", 800);
                if (!string.IsNullOrWhiteSpace(idn) &&
                    idn.IndexOf("973A", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.Add(address);
                    AppLog.Info("GpibScan", $"发现 DAQ973A：{address}（{idn}）");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("GpibScan", $"读取 {address} 的 *IDN? 失败：{ex.Message}");
            }
        }
        return result;
    }
}
