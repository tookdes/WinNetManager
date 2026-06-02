using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    private string RunPowerShell(string script, out string error, int timeoutMs = 30000)
        => ProcessRunner.RunPowerShell(script, out error, timeoutMs);

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
                string entry = line.TrimEnd(',').Trim();
                var parts = entry.Split(',');
                if (parts.Length >= 3)
                {
                    string dest = parts[0].Trim();
                    string prefixLen = parts.Length >= 4 ? parts[3].Trim() : "128";
                    string hop = parts[2].Trim();
                    routes.Add(new RouteEntry
                    {
                        AddressFamily = "IPv6",
                        DestinationPrefix = $"{dest}/{prefixLen}",
                        NextHop = hop,
                        InterfaceAlias = "",
                        InterfaceIndex = "",
                        RouteMetric = "1",
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
        string script = $"& {netshCmd}";
        string error;
        string output = RunPowerShell(script, out error);

        string combined = (output + " " + error).Trim();
        // netsh 成功时输出 "Ok." 或 "确定。"（精确匹配，避免误匹配含 "ok" 的单词）
        if (combined == "Ok." || combined == "确定。" || combined == "Ok" || combined == "确定")
            return new RouteCommandResult { Success = true, Message = combined };

        string msg = combined;
        if (ContainsIgnoreCase(msg, "Access is denied") || ContainsIgnoreCase(msg, "拒绝访问") || ContainsIgnoreCase(msg, "需要提升的权限"))
            msg = "需要以管理员身份运行本程序。";
        else if (ContainsIgnoreCase(msg, "already exists") || ContainsIgnoreCase(msg, "已存在"))
            msg = "该路由已存在。";

        return new RouteCommandResult { Success = false, Message = msg };
    }


    private string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        result.Add(sb.ToString().Trim());
        return result.ToArray();
    }
}
