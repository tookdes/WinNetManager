using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using WinNetManager.Models;

namespace WinNetManager.Services;

public class DhcpManager
{
    public List<NetworkAdapterInfo> GetAdapters()
    {
        var adapters = new List<NetworkAdapterInfo>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                       n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .OrderBy(n => n.Name, Comparer<string>.Create(NaturalStringComparer.CompareStrings)))
        {
            var props = ni.GetIPProperties();
            var ipv4Infos = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .ToList();
            var ipv6Addrs = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal)
                .Select(a => a.Address.ToString())
                .ToList();

            var gateways = props.GatewayAddresses
                .Where(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(g => g.Address.ToString())
                .Distinct()
                .ToList();

            string prefix = "";
            if (ipv4Infos.Count > 0)
                prefix = ipv4Infos[0].PrefixLength.ToString();

            adapters.Add(new NetworkAdapterInfo
            {
                Name = ni.Name,
                Description = ni.Description,
                Status = ni.OperationalStatus.ToString(),
                MacAddress = FormatMac(ni.GetPhysicalAddress().ToString()),
                IPv4Address = string.Join(", ", ipv4Infos.Select(a => a.Address.ToString())),
                IPv6Address = string.Join(", ", ipv6Addrs),
                Gateway = string.Join(", ", gateways),
                PrefixLength = prefix,
                DhcpEnabled = ipv4Infos.Count > 0 && props.GetIPv4Properties()?.IsDhcpEnabled == true,
            });
        }

        return adapters;
    }

    private static string FormatMac(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length != 12) return raw;
        var sb = new StringBuilder(17);
        for (int i = 0; i < 12; i += 2)
        {
            if (i > 0) sb.Append('-');
            sb.Append(raw, i, 2);
        }
        return sb.ToString();
    }

    private static string QuotePreviewArg(string arg) => $"\"{arg.Replace("\"", "\"\"")}\"";

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
        ProcessRunner.RunPowerShell(script, out error, 20000);

        if (!string.IsNullOrEmpty(error) && !CI(error, "警告") && !CI(error, "Warning"))
            return new DhcpResult { Success = false, Message = error.Trim() };

        return new DhcpResult { Success = true, Message = "网卡已成功重启。" };
    }

    public static string GetSetStaticCommandPreview(string adapterName, string ip, int prefixLength, string? gateway)
    {
        string alias = ProcessRunner.EscapePsSingleQuoted(adapterName);
        string safeIp = ProcessRunner.EscapePsSingleQuoted(ip);
        var sb = new StringBuilder();
        sb.AppendLine($"Set-NetIPInterface -InterfaceAlias '{alias}' -AddressFamily IPv4 -Dhcp Disabled");
        sb.AppendLine($"# 先添加新地址（已存在则跳过），再删除旧地址/旧网关；失败时自动恢复 DHCP");
        sb.AppendLine($"New-NetIPAddress -InterfaceAlias '{alias}' -IPAddress '{safeIp}' -PrefixLength {prefixLength} -Confirm:$false");
        sb.AppendLine($"Get-NetIPAddress -InterfaceAlias '{alias}' -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {{ $_.IPAddress -ne '{safeIp}' }} | Remove-NetIPAddress -Confirm:$false");
        sb.AppendLine($"Get-NetRoute -InterfaceAlias '{alias}' -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false");
        if (string.IsNullOrWhiteSpace(gateway))
            sb.AppendLine($"# 不设置默认网关");
        else
            sb.AppendLine($"New-NetRoute -InterfaceAlias '{alias}' -DestinationPrefix '0.0.0.0/0' -NextHop '{ProcessRunner.EscapePsSingleQuoted(gateway)}' -RouteMetric 1 -Confirm:$false");
        return sb.ToString();
    }

    public DhcpResult SetStaticIPv4(string adapterName, string ip, int prefixLength, string? gateway)
    {
        string alias = ProcessRunner.EscapePsSingleQuoted(adapterName);
        string safeIp = ProcessRunner.EscapePsSingleQuoted(ip);
        string script = BuildSetStaticScript(alias, safeIp, prefixLength, ProcessRunner.EscapePsSingleQuoted(gateway ?? ""));

        string error;
        string output = ProcessRunner.RunPowerShell(script, out error, 45000);

        if (!string.IsNullOrEmpty(error) && !CI(error, "警告") && !CI(error, "Warning"))
            return new DhcpResult { Success = false, Message = error.Trim() };

        return new DhcpResult
        {
            Success = true,
            Message = string.IsNullOrWhiteSpace(output) ? "静态 IP 已应用。" : output.Trim()
        };
    }

    /// <summary>
    /// 构建「设为静态 IP」的 PowerShell 脚本。
    /// 采用「先添加新地址、再删除旧地址」的顺序，
    /// 避免旧地址被删除后新地址添加失败导致断网；
    /// 任一步骤失败时自动恢复 DHCP，防止网卡失联。
    /// </summary>
    private static string BuildSetStaticScript(string alias, string ip, int prefixLength, string gateway)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$alias = '{alias}'; $ip = '{ip}'; $prefix = {prefixLength}; $gw = '{gateway}'");
        sb.AppendLine("try {");
        sb.AppendLine("  Set-NetIPInterface -InterfaceAlias $alias -AddressFamily IPv4 -Dhcp Disabled");
        sb.AppendLine("  $cur = Get-NetIPAddress -InterfaceAlias $alias -AddressFamily IPv4 -ErrorAction SilentlyContinue");
        sb.AppendLine("  $hasNew = $cur | Where-Object { $_.IPAddress -eq $ip }");
        sb.AppendLine("  if (-not $hasNew) {");
        sb.AppendLine("    New-NetIPAddress -InterfaceAlias $alias -IPAddress $ip -PrefixLength $prefix -Confirm:$false");
        sb.AppendLine("  } elseif ($hasNew.PrefixLength -ne $prefix) {");
        sb.AppendLine("    Remove-NetIPAddress -InterfaceAlias $alias -IPAddress $ip -Confirm:$false");
        sb.AppendLine("    New-NetIPAddress -InterfaceAlias $alias -IPAddress $ip -PrefixLength $prefix -Confirm:$false");
        sb.AppendLine("  }");
        sb.AppendLine("  $cur | Where-Object { $_.IPAddress -ne $ip } | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue");
        sb.AppendLine("  Get-NetRoute -InterfaceAlias $alias -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue");
        sb.AppendLine("  if (-not [string]::IsNullOrWhiteSpace($gw)) {");
        sb.AppendLine("    New-NetRoute -InterfaceAlias $alias -DestinationPrefix '0.0.0.0/0' -NextHop $gw -RouteMetric 1 -Confirm:$false");
        sb.AppendLine("  }");
        sb.AppendLine("} catch {");
        sb.AppendLine("  # 失败时恢复 DHCP，避免网卡失联");
        sb.AppendLine("  Set-NetIPInterface -InterfaceAlias $alias -AddressFamily IPv4 -Dhcp Enabled -ErrorAction SilentlyContinue");
        sb.AppendLine("  throw");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string GetEnableDhcpCommandPreview(string adapterName)
    {
        string alias = ProcessRunner.EscapePsSingleQuoted(adapterName);
        return
            $"Set-NetIPInterface -InterfaceAlias '{alias}' -AddressFamily IPv4 -Dhcp Enabled\n" +
            $"Get-NetRoute -InterfaceAlias '{alias}' -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false";
    }

    public DhcpResult EnableDhcpIPv4(string adapterName)
    {
        string alias = ProcessRunner.EscapePsSingleQuoted(adapterName);
        string script =
            $"Set-NetIPInterface -InterfaceAlias '{alias}' -AddressFamily IPv4 -Dhcp Enabled; " +
            $"Get-NetRoute -InterfaceAlias '{alias}' -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false";

        string error;
        string output = ProcessRunner.RunPowerShell(script, out error, 20000);

        if (!string.IsNullOrEmpty(error) && !CI(error, "警告") && !CI(error, "Warning"))
            return new DhcpResult { Success = false, Message = error.Trim() };

        return new DhcpResult
        {
            Success = true,
            Message = string.IsNullOrWhiteSpace(output) ? "已切换为 DHCP。" : output.Trim()
        };
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

    private static DhcpResult RunChainCommand(string releaseSwitch, string renewSwitch, string adapterName, string protocolLabel)
    {
        var outputParts = new List<string>();

        var release = RunIpConfig(releaseSwitch, adapterName, 60000);
        if (!string.IsNullOrWhiteSpace(release.Message))
            outputParts.Add(release.Message);

        var renew = RunIpConfig(renewSwitch, adapterName, 60000);
        if (!string.IsNullOrWhiteSpace(renew.Message))
            outputParts.Add(renew.Message);

        if (!release.Success && !renew.Success)
        {
            return new DhcpResult
            {
                Success = false,
                Message = $"{protocolLabel} Release+Renew 均失败，网卡可能处于无地址状态。\n\n{string.Join("\n", outputParts)}"
            };
        }

        if (!release.Success)
        {
            // 释放失败但续租成功：终态正常，视为成功
            return new DhcpResult
            {
                Success = true,
                Message = $"{protocolLabel} 释放失败，但续租成功（终态正常）。\n\n{string.Join("\n", outputParts)}"
            };
        }

        if (!renew.Success)
        {
            return new DhcpResult
            {
                Success = false,
                Message = $"{protocolLabel} 释放成功但续租失败，网卡可能暂无 IPv{protocolLabel[^1]} 地址。\n\n{string.Join("\n", outputParts)}"
            };
        }

        return new DhcpResult
        {
            Success = true,
            Message = $"{protocolLabel} Release+Renew 完成。\n\n{string.Join("\n", outputParts)}"
        };
    }
}

public class DhcpResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
