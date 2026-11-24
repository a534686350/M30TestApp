using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ivi.Visa.Interop;
using M30TestApp.Core.Common;
using M30TestApp.Core.Devices.Hw;

namespace M30TestApp.Core.Config;

/// <summary>
/// GPIB / VISA 仪器扫描 —— 自动发现总线上的 DAQ973A，避免现场手填地址。
///
/// 底层用 NI-VISA 的 <c>ResourceManager.FindRsrc("GPIB?*INSTR")</c> 枚举资源，
/// 再逐个发 <c>*IDN?</c> 识别型号（含 "973A" 即认为是目标万用表）。
///
/// NI-VISA 未安装 / 驱动异常时静默返回空列表 —— 现场可继续手填地址，不能被扫描拖垮。
/// </summary>
public static class VisaScanner
{
    private static readonly Regex GpibAddressPattern =
        new(@"^GPIB\d+::\d+::INSTR$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>扫描所有 GPIB INSTR 资源（不做型号识别）。</summary>
    public static IReadOnlyList<string> FindGpibResources()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
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

            var resources = rm.FindRsrc("GPIB?*INSTR");
            if (resources is Array arr)
            {
                foreach (var item in arr)
                {
                    var s = Convert.ToString(item)?.Trim();
                    if (!string.IsNullOrWhiteSpace(s) && GpibAddressPattern.IsMatch(s))
                        found.Add(s);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("GpibScan", $"枚举 GPIB 资源失败：{ex.Message}");
        }
        return found.ToArray();
    }

    /// <summary>
    /// 扫描并识别 DAQ973A。对每个 GPIB 资源发 *IDN?，保留型号串含 "973A" 的地址。
    /// 识别失败（设备不开机 / 非 973）不影响其它资源。
    /// </summary>
    public static IReadOnlyList<string> FindDmm973()
    {
        var result = new List<string>();
        foreach (var address in FindGpibResources())
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
