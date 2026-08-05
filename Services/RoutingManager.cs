using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WinNetManager.Models;

namespace WinNetManager.Services;

public class RouteCommandResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class NetInterface
{
    public string InterfaceAlias { get; set; } = "";
    public string InterfaceIndex { get; set; } = "";
    public string AddressFamily { get; set; } = "";
}

public class RoutingManager
{
    private static readonly string[] ValidAddressFamilies = { "IPv4", "IPv6" };

    private string RunPowerShell(string script, out string error, int timeoutMs = 30000)
        => ProcessRunner.RunPowerShell(script, out error, timeoutMs);

    public static bool ValidateRoute(RouteEntry route, out string error)
    {
        error = "";
        if (route == null)
        {
            error = "路由为空。";
            return false;
        }

        string family = route.AddressFamily?.Trim() ?? "";
        if (!ValidAddressFamilies.Contains(family, StringComparer.OrdinalIgnoreCase))
        {
            error = "地址族必须为 IPv4 或 IPv6。";
            return false;
        }

        route.AddressFamily = family.Equals("IPv6", StringComparison.OrdinalIgnoreCase) ? "IPv6" : "IPv4";

        if (!IsValidDestinationPrefix(route.DestinationPrefix, route.AddressFamily))
        {
            error = route.AddressFamily == "IPv6"
                ? "目标前缀必须是有效的 IPv6 CIDR，例如 2400::/48。"
                : "目标前缀必须是有效的 IPv4 CIDR，例如 192.168.1.0/24。";
            return false;
        }

        if (!IPAddress.TryParse(route.NextHop?.Trim(), out var nextHop))
        {
            error = "下一跳必须是有效 IP 地址。";
            return false;
        }

        if (route.AddressFamily == "IPv4" && nextHop.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "IPv4 路由的下一跳必须是 IPv4 地址。";
            return false;
        }

        if (route.AddressFamily == "IPv6" && nextHop.AddressFamily != AddressFamily.InterNetworkV6)
        {
            error = "IPv6 路由的下一跳必须是 IPv6 地址。";
            return false;
        }

        route.NextHop = nextHop.ToString();

        string alias = route.InterfaceAlias?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(alias) || ContainsControlChars(alias))
        {
            error = "接口别名不能为空，且不能包含控制字符。";
            return false;
        }
        route.InterfaceAlias = alias;

        if (!int.TryParse(route.RouteMetric?.Trim(), out int metric) || metric < 0 || metric > 999999)
        {
            error = "度量值必须是 0 到 999999 之间的整数。";
            return false;
        }
        route.RouteMetric = metric.ToString();

        return true;
    }

    private static bool IsValidDestinationPrefix(string? prefix, string family)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return false;

        string trimmed = prefix.Trim();
        int slashIndex = trimmed.LastIndexOf('/');
        if (slashIndex <= 0 || slashIndex == trimmed.Length - 1) return false;

        string addressPart = trimmed[..slashIndex];
        string prefixPart = trimmed[(slashIndex + 1)..];
        if (!IPAddress.TryParse(addressPart, out var ip)) return false;
        if (!int.TryParse(prefixPart, out int prefixLength)) return false;

        if (family == "IPv4")
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            if (prefixLength < 0 || prefixLength > 32) return false;
        }
        else
        {
            if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
            if (prefixLength < 0 || prefixLength > 128) return false;
        }

        return true;
    }

    private static bool ContainsControlChars(string value)
        => value.Any(char.IsControl);

    public List<RouteEntry> GetPersistentRoutes(string? addressFamily = null)
    {
        var routes = new List<RouteEntry>();

        string familyFilter = string.IsNullOrEmpty(addressFamily)
            ? ""
            : $" -AddressFamily {addressFamily}";

        string script =
            $"Get-NetRoute{familyFilter} -PolicyStore PersistentStore | " +
            "Select-Object AddressFamily,DestinationPrefix,NextHop,InterfaceAlias,InterfaceIndex,RouteMetric | " +
            "ConvertTo-Csv -NoTypeInformation";

        string error;
        string output = RunPowerShell(script, out error);

        // -PolicyStore 可能不支持，fallback 到注册表
        if (!string.IsNullOrEmpty(error) && (ContainsIgnoreCase(error, "Invalid parameter") || ContainsIgnoreCase(error, "参数无效")))
            return GetPersistentRoutesViaRoutePrint(addressFamily);

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return routes;

        string[] headers = ParseCsvLine(lines[0]);
        int idxFamily = Array.IndexOf(headers, "AddressFamily");
        int idxPrefix = Array.IndexOf(headers, "DestinationPrefix");
        int idxNextHop = Array.IndexOf(headers, "NextHop");
        int idxAlias = Array.IndexOf(headers, "InterfaceAlias");
        int idxIndex = Array.IndexOf(headers, "InterfaceIndex");
        int idxMetric = Array.IndexOf(headers, "RouteMetric");

        for (int i = 1; i < lines.Length; i++)
        {
            string[] values = ParseCsvLine(lines[i]);
            if (values.Length < Math.Max(idxPrefix, Math.Max(idxNextHop, idxAlias)) + 1) continue;

            routes.Add(new RouteEntry
            {
                AddressFamily = idxFamily >= 0 && idxFamily < values.Length ? values[idxFamily] : "",
                DestinationPrefix = idxPrefix >= 0 && idxPrefix < values.Length ? values[idxPrefix] : "",
                NextHop = idxNextHop >= 0 && idxNextHop < values.Length ? values[idxNextHop] : "",
                InterfaceAlias = idxAlias >= 0 && idxAlias < values.Length ? values[idxAlias] : "",
                InterfaceIndex = idxIndex >= 0 && idxIndex < values.Length ? values[idxIndex] : "",
                RouteMetric = idxMetric >= 0 && idxMetric < values.Length ? values[idxMetric] : "",
                Store = "PersistentStore"
            });
        }

        return routes;
    }

    /// <summary>
    /// Fallback: read persistent routes from the registry
    /// HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\PersistentRoutes
    /// Each line: "dest,mask,nexthop,metric"
    /// </summary>
    private List<RouteEntry> GetPersistentRoutesViaRoutePrint(string? addressFamily)
    {
        var routes = new List<RouteEntry>();
        bool wantV6 = addressFamily == "IPv6";
        bool wantV4 = string.IsNullOrEmpty(addressFamily) || addressFamily == "IPv4";

        // Read IPv4 persistent routes from registry
        if (wantV4)
        {
            string script =
                "Get-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\PersistentRoutes' -ErrorAction SilentlyContinue | " +
                "Get-Member -MemberType NoteProperty | Where-Object { $_.Name -notmatch '^PS' } | ForEach-Object { " +
                "$val = (Get-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\PersistentRoutes' -Name $_.Name).($_.Name); " +
                "$_.Name + ',' + $val }";
            string error;
            string output = RunPowerShell(script, out error);

            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // Format: "dest,mask,nexthop,metric,"  (key=name, value=empty or trailing comma)
                // Actually registry value name = "dest,mask,nexthop,metric" and value = ""
                string entry = line.TrimEnd(',').Trim();
                var parts = entry.Split(',');
                if (parts.Length >= 3)
                {
                    string dest = parts[0].Trim();
                    string mask = parts[1].Trim();
                    string hop = parts[2].Trim();
                    string metric = parts.Length >= 4 ? parts[3].Trim() : "1";
                    routes.Add(new RouteEntry
                    {
                        AddressFamily = "IPv4",
                        DestinationPrefix = $"{dest}/{MaskToCidr(mask)}",
                        NextHop = hop,
                        InterfaceAlias = "",
                        InterfaceIndex = "",
                        RouteMetric = metric,
                        Store = "PersistentStore"
                    });
                }
            }
        }

        // IPv6 persistent routes are in:
        // HKLM\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\PersistentRoutes
        if (wantV6)
        {
            string script =
                "Get-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip6\\Parameters\\PersistentRoutes' -ErrorAction SilentlyContinue | " +
                "Get-Member -MemberType NoteProperty | Where-Object { $_.Name -notmatch '^PS' } | ForEach-Object { " +
                "$val = (Get-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip6\\Parameters\\PersistentRoutes' -Name $_.Name).($_.Name); " +
                "$_.Name + ',' + $val }";
            string error;
            string output = RunPowerShell(script, out error);

            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // IPv6 注册表格式：值名 = "dest/prefixlen"，值数据 = "nexthop,,metric"
                // PowerShell 输出：dest/prefixlen,nexthop,,metric
                string entry = line.TrimEnd(',').Trim();
                var parts = entry.Split(',');
                if (parts.Length >= 2)
                {
                    string destPart = parts[0].Trim(); // dest/prefixlen
                    string hop = parts[1].Trim();       // nexthop
                    string metric = parts.Length >= 4 ? parts[3].Trim() : "1";

                    // destPart 可能已包含 prefixlen（如 "fe80::/10"），也可能是纯地址
                    string destPrefix = destPart.Contains('/') ? destPart : $"{destPart}/128";

                    routes.Add(new RouteEntry
                    {
                        AddressFamily = "IPv6",
                        DestinationPrefix = destPrefix,
                        NextHop = hop,
                        InterfaceAlias = "",
                        InterfaceIndex = "",
                        RouteMetric = string.IsNullOrEmpty(metric) ? "1" : metric,
                        Store = "PersistentStore"
                    });
                }
            }
        }

        return routes;
    }

    private static bool ContainsIgnoreCase(string source, string value)
        => source?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

    private static int MaskToCidr(string mask)
    {
        if (!System.Net.IPAddress.TryParse(mask, out var ip)) return 32;
        byte[] bytes = ip.GetAddressBytes();
        int cidr = 0;
        foreach (byte b in bytes)
        {
            int v = b;
            while (v != 0) { cidr++; v &= v - 1; }
        }
        return cidr;
    }

    public List<NetInterface> GetInterfaces()
    {
        var interfaces = new List<NetInterface>();

        string script =
            "Get-NetIPInterface | " +
            "Select-Object InterfaceAlias,InterfaceIndex,AddressFamily | " +
            "ConvertTo-Csv -NoTypeInformation";

        string error;
        string output = RunPowerShell(script, out error);

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return interfaces;

        string[] headers = ParseCsvLine(lines[0]);
        int idxAlias = Array.IndexOf(headers, "InterfaceAlias");
        int idxIndex = Array.IndexOf(headers, "InterfaceIndex");
        int idxFamily = Array.IndexOf(headers, "AddressFamily");

        for (int i = 1; i < lines.Length; i++)
        {
            string[] values = ParseCsvLine(lines[i]);
            if (values.Length < 2) continue;

            string family = idxFamily >= 0 && idxFamily < values.Length ? values[idxFamily] : "";
            string familyName = family == "2" ? "IPv4" : (family == "23" ? "IPv6" : family);

            interfaces.Add(new NetInterface
            {
                InterfaceAlias = idxAlias >= 0 && idxAlias < values.Length ? values[idxAlias] : "",
                InterfaceIndex = idxIndex >= 0 && idxIndex < values.Length ? values[idxIndex] : "",
                AddressFamily = familyName
            });
        }

        return interfaces;
    }

    public RouteCommandResult AddRoute(RouteEntry route)
    {
        if (!ValidateRoute(route, out string validationError))
            return new RouteCommandResult { Success = false, Message = validationError };

        string prefix = route.DestinationPrefix ?? "";
        string safeAlias = ProcessRunner.EscapePsSingleQuoted(route.InterfaceAlias);
        string safeHop = ProcessRunner.EscapePsSingleQuoted(route.NextHop);
        string metric = route.RouteMetric ?? "1";

        string cmd;
        if (route.AddressFamily == "IPv6")
            cmd = $"netsh interface ipv6 add route prefix={prefix} interface='{safeAlias}' nexthop={safeHop} metric={metric} store=persistent";
        else
            cmd = $"netsh interface ipv4 add route {prefix} interface='{safeAlias}' nexthop={safeHop} metric={metric} store=persistent";

        return ExecuteNetsh(cmd);
    }

    public RouteCommandResult DeleteRoute(RouteEntry route)
    {
        if (!ValidateRoute(route, out string validationError))
            return new RouteCommandResult { Success = false, Message = validationError };

        string prefix = route.DestinationPrefix ?? "";
        string safeAlias = ProcessRunner.EscapePsSingleQuoted(route.InterfaceAlias);
        string safeHop = ProcessRunner.EscapePsSingleQuoted(route.NextHop);

        string cmd;
        if (route.AddressFamily == "IPv6")
            cmd = $"netsh interface ipv6 delete route prefix={prefix} interface='{safeAlias}' nexthop={safeHop} store=persistent";
        else
            cmd = $"netsh interface ipv4 delete route {prefix} interface='{safeAlias}' nexthop={safeHop} store=persistent";

        return ExecuteNetsh(cmd);
    }

    private RouteCommandResult ExecuteNetsh(string netshCmd)
    {
        // 用 $LASTEXITCODE 判断 netsh 是否成功，避免依赖本地化输出文本（Ok./确定。）
        string script = $"{netshCmd}; exit $LASTEXITCODE";
        string error;
        string output = ProcessRunner.RunPowerShell(script, out error, out int exitCode, 30000);

        bool hasRealError = !string.IsNullOrWhiteSpace(error)
            && error.IndexOf("警告", StringComparison.OrdinalIgnoreCase) < 0
            && error.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) < 0;

        if (exitCode == 0 && !hasRealError)
            return new RouteCommandResult { Success = true, Message = output.Trim() };

        string msg = (output + " " + error).Trim();
        if (string.IsNullOrWhiteSpace(msg))
            msg = $"命令执行失败（退出码 {exitCode}）。";
        if (ContainsIgnoreCase(msg, "Access is denied") || ContainsIgnoreCase(msg, "拒绝访问") || ContainsIgnoreCase(msg, "需要提升的权限"))
            msg = "需要以管理员身份运行本程序。";
        else if (ContainsIgnoreCase(msg, "already exists") || ContainsIgnoreCase(msg, "已存在"))
            msg = "该路由已存在。";

        return new RouteCommandResult { Success = false, Message = msg };
    }


    private string[] ParseCsvLine(string line) => CsvParser.ParseLine(line);
}
