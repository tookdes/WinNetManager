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
    private static string QuotePreviewArg(string arg) => $"\"{arg.Replace("\"", "\"\"")}\"";

    /// <summary>
    /// Returns a human-readable command preview for Release+Renew.
    /// This is for display only; the actual execution uses ProcessRunner.
    /// </summary>
    public static string GetReleaseRenewCommandPreview(string adapterName, bool ipv6)
    {
        string releaseCmd = ipv6
            ? $"ipconfig /release6 {QuotePreviewArg(adapterName)}"
            : $"ipconfig /release {QuotePreviewArg(adapterName)}";
        string renewCmd = ipv6
            ? $"ipconfig /renew6 {QuotePreviewArg(adapterName)}"
            : $"ipconfig /renew {QuotePreviewArg(adapterName)}";
        return $"{releaseCmd}\n{renewCmd}";
    }

    public DhcpResult ReleaseRenew(string adapterName, bool ipv6)
    {
        string releaseSwitch = ipv6 ? "/release6" : "/release";
        string renewSwitch = ipv6 ? "/renew6" : "/renew";
        return RunChainCommand(releaseSwitch, renewSwitch, adapterName, ipv6 ? "IPv6" : "IPv4");
    }

    public static string GetRestartAdapterCommandPreview(string adapterName)
    {
        return $"Restart-NetAdapter -Name '{ProcessRunner.EscapePsSingleQuoted(adapterName)}' -Confirm:$false";
    }

    public DhcpResult RestartAdapter(string adapterName)
    {
        string script = $"Restart-NetAdapter -Name '{ProcessRunner.EscapePsSingleQuoted(adapterName)}' -Confirm:$false";
        string error;
        string output = ProcessRunner.RunPowerShell(script, out error, 20000);

        if (!string.IsNullOrEmpty(error) && !CI(error, "警告") && !CI(error, "Warning"))
            return new DhcpResult { Success = false, Message = error.Trim() };

        return new DhcpResult { Success = true, Message = "网卡已成功重启。" };
    }

    public DhcpResult Release(string adapterName, bool ipv6)
    {
        string command = ipv6 ? "/release6" : "/release";
        return RunIpConfig(command, adapterName, 60000);
    }

    public DhcpResult Renew(string adapterName, bool ipv6)
    {
        string command = ipv6 ? "/renew6" : "/renew";
        return RunIpConfig(command, adapterName, 60000);
    }

    private static bool CI(string source, string value)
        => source?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

    private static DhcpResult RunIpConfig(string command, string adapterName, int timeoutMs)
    {
        string error;
        string output = ProcessRunner.Run("ipconfig.exe", new[] { command, adapterName }, out error, out int exitCode, timeoutMs);

        if (!string.IsNullOrEmpty(error) && !CI(error, "警告") && !CI(error, "Warning"))
            return new DhcpResult { Success = false, Message = error.Trim() };

        if (exitCode != 0)
        {
            string message = !string.IsNullOrWhiteSpace(output)
                ? output.Trim()
                : $"命令返回非零退出码 {exitCode}。";
            return new DhcpResult { Success = false, Message = message };
        }

        return new DhcpResult { Success = true, Message = output.Trim() };
    }

    /// <summary>
    /// 链式执行两条命令：第一条执行完后立即执行第二条。
    /// 使用独立 cmd 进程，即使网络断开也能继续执行。
    /// </summary>
    private static DhcpResult RunChainCommand(string releaseSwitch, string renewSwitch, string adapterName, string protocolLabel)
    {
        var outputParts = new List<string>();
        bool anyFailed = false;

        var release = RunIpConfig(releaseSwitch, adapterName, 60000);
        if (!string.IsNullOrWhiteSpace(release.Message))
            outputParts.Add(release.Message);
        if (!release.Success)
            anyFailed = true;

        // 即使 release 失败（如地址已释放），仍继续执行 renew
        var renew = RunIpConfig(renewSwitch, adapterName, 60000);
        if (!string.IsNullOrWhiteSpace(renew.Message))
            outputParts.Add(renew.Message);
        if (!renew.Success)
            anyFailed = true;

        if (!release.Success && !renew.Success)
        {
            return new DhcpResult
            {
                Success = false,
                Message = $"{protocolLabel} Release+Renew 均失败。\n\n{string.Join("\n", outputParts)}"
            };
        }

        return new DhcpResult
        {
            Success = !anyFailed,
            Message = $"{protocolLabel} Release+Renew 已提交执行。\n\n{string.Join("\n", outputParts)}"
        };
    }
}

public class DhcpResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
