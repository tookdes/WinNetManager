using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using WinNetManager.Models;

namespace WinNetManager.Services;

public class DhcpManager
{
    public List<NetworkAdapterInfo> GetAdapters()
    {
        var adapters = new List<NetworkAdapterInfo>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up ||
                       n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                       n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .OrderBy(n => n.Name))
        {
            var props = ni.GetIPProperties();
            var ipv4Addrs = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToList();
            var ipv6Addrs = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal)
                .Select(a => a.Address.ToString())
                .ToList();

            adapters.Add(new NetworkAdapterInfo
            {
                Name = ni.Name,
                Description = ni.Description,
                Status = ni.OperationalStatus.ToString(),
                MacAddress = ni.GetPhysicalAddress().ToString(),
                IPv4Address = string.Join(", ", ipv4Addrs),
                IPv6Address = string.Join(", ", ipv6Addrs),
                DhcpEnabled = ipv4Addrs.Count > 0 && props.GetIPv4Properties()?.IsDhcpEnabled == true,
            });
        }

        return adapters;
    }

    /// <summary>
    /// 链式执行 release + renew，确保即使当前网络会话断开，后台进程仍能完成操作。
    /// </summary>
    private static string EscapeCmdArg(string arg) => arg.Replace("\"", "\"\"");

    /// <summary>
    /// Returns a human-readable command preview for Release+Renew.
    /// This is for display only; the actual execution uses ProcessRunner.
    /// </summary>
    public static string GetReleaseRenewCommandPreview(string adapterName, bool ipv6)
    {
        string safe = EscapeCmdArg(adapterName);
        string releaseCmd = ipv6
            ? $"ipconfig /release6 \"{safe}\""
            : $"ipconfig /release \"{safe}\"";
        string renewCmd = ipv6
            ? $"ipconfig /renew6 \"{safe}\""
            : $"ipconfig /renew \"{safe}\"";
        return $"cmd /c \"{releaseCmd} && {renewCmd}\"";
    }

    public DhcpResult ReleaseRenew(string adapterName, bool ipv6)
    {
        string safe = EscapeCmdArg(adapterName);
        string releaseCmd = ipv6
            ? $"ipconfig /release6 \"{safe}\""
            : $"ipconfig /release \"{safe}\"";
        string renewCmd = ipv6
            ? $"ipconfig /renew6 \"{safe}\""
            : $"ipconfig /renew \"{safe}\"";

        return RunChainCommand(releaseCmd, renewCmd, ipv6 ? "IPv6" : "IPv4");
    }

    public DhcpResult Release(string adapterName, bool ipv6)
    {
        string safe = EscapeCmdArg(adapterName);
        string cmd = ipv6
            ? $"ipconfig /release6 \"{safe}\""
            : $"ipconfig /release \"{safe}\"";
        return RunCommand(cmd);
    }

    public DhcpResult Renew(string adapterName, bool ipv6)
    {
        string safe = EscapeCmdArg(adapterName);
        string cmd = ipv6
            ? $"ipconfig /renew6 \"{safe}\""
            : $"ipconfig /renew \"{safe}\"";
        return RunCommand(cmd);
    }

    private static DhcpResult RunCommand(string command)
    {
        string error;
        string output = ProcessRunner.Run("cmd.exe", $"/c {command}", out error, 60000);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
            return new DhcpResult { Success = false, Message = error.Trim() };

        return new DhcpResult { Success = true, Message = output.Trim() };
    }

    /// <summary>
    /// 链式执行两条命令：第一条执行完后立即执行第二条。
    /// 使用独立 cmd 进程，即使网络断开也能继续执行。
    /// </summary>
    private static DhcpResult RunChainCommand(string firstCmd, string secondCmd, string protocolLabel)
    {
        string error;
        string output = ProcessRunner.Run("cmd.exe", $"/c \"{firstCmd} && {secondCmd}\"", out error, out int exitCode, 120000);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
            return new DhcpResult { Success = false, Message = error.Trim() };

        if (exitCode != 0)
        {
            string hint = output.Contains("无法") || output.Contains("error") || output.Contains("失败")
                ? output.Trim()
                : $"命令返回非零退出码 {exitCode}。";
            return new DhcpResult { Success = false, Message = hint };
        }

        return new DhcpResult
        {
            Success = true,
            Message = $"{protocolLabel} Release+Renew 已提交执行。\n\n{output.Trim()}"
        };
    }
}

public class DhcpResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
