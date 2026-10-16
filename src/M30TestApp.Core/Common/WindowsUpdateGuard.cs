using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;

namespace M30TestApp.Core.Common;

/// <summary>
/// 阻止 Windows 自动更新（避免测试跑到一半被系统重启）。
///
/// 现场一跑就是几小时甚至几天，中途被 Windows Update 偷偷重启一次，整批数据就废了。
/// 做两件事：① 注册表关掉自动更新 + 禁止自动重启；② 停掉并禁用 Windows Update 服务。
///
/// **需要管理员权限**：写 HKLM 策略和停服务都要。普通权限跑就返回明确的提示，
/// 让操作员右键「以管理员身份运行」，绝不静默失败。
/// </summary>
public static class WindowsUpdateGuard
{
    private const string AuPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    private const string WuService = "wuauserv";
    /// <summary>更新编排服务（Win10/11 实际干活的那个），一并停掉。</summary>
    private const string UsoService = "UsoSvc";

    /// <summary>当前是否处于"已阻止"状态。</summary>
    public static bool IsBlocked()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AuPath, writable: false);
            if (key is null) return false;
            var noAuto = key.GetValue("NoAutoUpdate");
            return noAuto is 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 打开阻止。返回给用户看的结果文字（成功说明做了什么，失败说明为什么）。
    /// </summary>
    public static string Enable()
    {
        var done = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(AuPath, writable: true)
                ?? throw new InvalidOperationException("无法创建/打开注册表 " + AuPath);

            // NoAutoUpdate=1 关掉自动更新；AUOptions=2 只通知不下载
            key.SetValue("NoAutoUpdate", 1, RegistryValueKind.DWord);
            key.SetValue("AUOptions", 2, RegistryValueKind.DWord);
            // 有人登录时也绝不自动重启
            key.SetValue("NoAutoRebootWithLoggedOnUsers", 1, RegistryValueKind.DWord);
            done.Add("已关闭自动更新并禁止自动重启");
        }
        catch (Exception ex)
        {
            return NotAdmin(ex);
        }

        // Windows Update 服务停掉并禁用（下次重启也要保持禁用）
        foreach (var svc in new[] { WuService, UsoService })
        {
            var stop = Run("net", $"stop {svc}");
            var disable = Run("sc", $"config {svc} start= disabled");
            // UsoSvc 停不掉是正常的（受保护），禁用成功就算数
            if (disable.Ok || stop.Ok)
                done.Add($"{svc} 已停止并设为禁用");
        }

        // 让策略立刻生效，不用等重启或手动 gpupdate
        Run("gpupdate", "/target:computer /force /quiet", 6000);

        return string.Join("；", done) + "。想恢复就把勾去掉。";
    }

    /// <summary>取消阻止，恢复 Windows 自动更新。</summary>
    public static string Disable()
    {
        var done = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AuPath, writable: true);
            if (key is not null)
            {
                foreach (var name in new[] { "NoAutoUpdate", "NoAutoRebootWithLoggedOnUsers" })
                    if (key.GetValue(name) is not null) key.DeleteValue(name);
                // AUOptions 还原成默认的"通知但不自动安装"
                key.SetValue("AUOptions", 2, RegistryValueKind.DWord);
            }
            done.Add("已恢复默认更新设置");
        }
        catch (Exception ex)
        {
            return NotAdmin(ex);
        }

        foreach (var svc in new[] { WuService, UsoService })
        {
            var enable = Run("sc", $"config {svc} start= demand");
            if (enable.Ok) done.Add($"{svc} 已恢复默认启动方式");
        }

        Run("gpupdate", "/target:computer /force /quiet", 6000);

        return string.Join("；", done) + "。";
    }

    private static string NotAdmin(Exception ex) =>
        "需要管理员权限：请关闭程序 → 右键 M30TestApp.V2.exe →「以管理员身份运行」→ 再勾一次。"
        + $"（系统返回：{ex.Message}）";

    private readonly record struct CmdResult(bool Ok, string Output);

    private static CmdResult Run(string file, string args, int timeoutMs = 10000)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return new CmdResult(false, "启动失败");

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { /* 超时就算了，别把界面卡死 */ }
                return new CmdResult(false, "超时");
            }
            // sc.exe / net.exe 成功时返回 0
            var text = (stdout + stderr).Trim();
            return new CmdResult(p.ExitCode == 0, text.Length > 0 ? text : $"exit={p.ExitCode}");
        }
        catch (Exception ex)
        {
            return new CmdResult(false, ex.Message);
        }
    }
}
