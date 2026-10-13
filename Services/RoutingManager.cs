using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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

internal sealed class DynamicRouteTaskMetadata
{
    public int Version { get; set; } = 1;
    public string AddressFamily { get; set; } = "IPv4";
    public string DestinationPrefix { get; set; } = "";
    public string InterfaceAlias { get; set; } = "";
    public string RouteMetric { get; set; } = "1";
}


public class RoutingManager
{
    private static readonly string[] ValidAddressFamilies = { "IPv4", "IPv6" };
    private const string DynamicTaskPrefix = "WinNetManager_DynamicRoute_";
    private const string DynamicDescriptionPrefix = "WinNetManager.DynamicRoute.v1:";

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

        string alias = route.InterfaceAlias?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(alias) || ContainsControlChars(alias))
        {
            error = "接口别名不能为空，且不能包含控制字符。";
            return false;
        }
        route.InterfaceAlias = alias;

        if (route.IsDynamicGateway)
        {
            if (route.AddressFamily != "IPv4")
            {
                error = "动态 DHCP 网关路由当前仅支持 IPv4；IPv6 默认路由通常由 Router Advertisement 管理。";
                return false;
            }

            // Dynamic routes intentionally allow an empty NextHop: the task resolves the
            // selected interface's current default gateway at apply/run time.
            if (!string.IsNullOrWhiteSpace(route.NextHop))
            {
                if (!IPAddress.TryParse(route.NextHop.Trim(), out var observedHop) ||
                    observedHop.AddressFamily != AddressFamily.InterNetwork)
                {
                    route.NextHop = "";
                }
                else
                {
                    route.NextHop = observedHop.ToString();
                }
            }
        }
        else
        {
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
        }

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
            return MergeDynamicManagedRoutes(GetPersistentRoutesViaRoutePrint(addressFamily), addressFamily);

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return MergeDynamicManagedRoutes(routes, addressFamily);

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

        return MergeDynamicManagedRoutes(routes, addressFamily);
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


    private List<RouteEntry> MergeDynamicManagedRoutes(List<RouteEntry> routes, string? addressFamily)
    {
        List<RouteEntry> managed;
        try { managed = GetDynamicManagedRoutes(); }
        catch { return routes; }

        foreach (var dynamicRoute in managed)
        {
            if (!string.IsNullOrWhiteSpace(addressFamily) &&
                !string.Equals(dynamicRoute.AddressFamily, addressFamily, StringComparison.OrdinalIgnoreCase))
                continue;

            var existing = routes.FirstOrDefault(r =>
                string.Equals(r.AddressFamily, dynamicRoute.AddressFamily, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.DestinationPrefix, dynamicRoute.DestinationPrefix, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.InterfaceAlias, dynamicRoute.InterfaceAlias, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.IsDynamicGateway = true;
                existing.DynamicCurrentGateway = dynamicRoute.DynamicCurrentGateway;
                existing.DynamicTaskStatus = dynamicRoute.DynamicTaskStatus;
                existing.RouteMetric = dynamicRoute.RouteMetric;
                existing.Store = "DynamicTask+PersistentStore";
            }
            else
            {
                routes.Add(dynamicRoute);
            }
        }

        return routes;
    }

    private List<RouteEntry> GetDynamicManagedRoutes()
    {
        var routes = new List<RouteEntry>();
        string output = ProcessRunner.RunNative(
            "schtasks.exe",
            "/Query /FO CSV /NH",
            out _,
            out int exitCode,
            20000);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
            return routes;

        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = CsvParser.ParseLine(line);
            if (fields.Length == 0)
                continue;

            string taskPath = fields[0].Trim();
            string leafName = taskPath.TrimStart('\\')
                .Split('\\', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault() ?? "";

            if (!leafName.StartsWith(DynamicTaskPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string xml = ProcessRunner.RunNative(
                "schtasks.exe",
                $"/Query /TN \"{taskPath.Replace("\"", "")}\" /XML",
                out _,
                out int xmlExit,
                15000);

            if (xmlExit != 0 || string.IsNullOrWhiteSpace(xml))
                continue;

            DynamicRouteTaskMetadata? metadata = ParseDynamicTaskMetadata(xml);
            if (metadata == null || metadata.Version != 1)
                continue;

            bool enabled = ParseTaskEnabled(xml);
            string currentGateway = GetCurrentDefaultGateway(metadata.InterfaceAlias);
            string installedHop = GetInstalledDynamicRouteNextHop(
                metadata.InterfaceAlias,
                metadata.DestinationPrefix);

            routes.Add(new RouteEntry
            {
                AddressFamily = metadata.AddressFamily,
                DestinationPrefix = metadata.DestinationPrefix,
                NextHop = installedHop,
                InterfaceAlias = metadata.InterfaceAlias,
                RouteMetric = metadata.RouteMetric,
                Store = "DynamicTask",
                IsDynamicGateway = true,
                DynamicCurrentGateway = currentGateway,
                DynamicTaskStatus = enabled ? "已注册/启用" : "已注册/禁用"
            });
        }

        return routes;
    }

    private static DynamicRouteTaskMetadata? ParseDynamicTaskMetadata(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            string description = doc.Descendants(ns + "Description").FirstOrDefault()?.Value ?? "";
            if (!description.StartsWith(DynamicDescriptionPrefix, StringComparison.Ordinal))
                return null;

            string payload = description[DynamicDescriptionPrefix.Length..];
            byte[] jsonBytes = Convert.FromBase64String(payload);
            return JsonSerializer.Deserialize<DynamicRouteTaskMetadata>(
                Encoding.UTF8.GetString(jsonBytes));
        }
        catch
        {
            return null;
        }
    }

    private static bool ParseTaskEnabled(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            string? enabled = doc.Root?
                .Element(ns + "Settings")?
                .Element(ns + "Enabled")?
                .Value;
            return !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    public string GetCurrentDefaultGateway(string interfaceAlias)
    {
        if (string.IsNullOrWhiteSpace(interfaceAlias))
            return "";

        string safeAlias = ProcessRunner.EscapePsSingleQuoted(interfaceAlias);
        string script =
            $"$r = Get-NetRoute -PolicyStore ActiveStore -AddressFamily IPv4 -InterfaceAlias '{safeAlias}' " +
            "-DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.NextHop -and $_.NextHop -ne '0.0.0.0' } | " +
            "Sort-Object RouteMetric | Select-Object -First 1; " +
            "if($r){ $r.NextHop }";

        string output = RunPowerShell(script, out _, 15000).Trim();
        return IPAddress.TryParse(output, out var gateway) &&
               gateway.AddressFamily == AddressFamily.InterNetwork
            ? gateway.ToString()
            : "";
    }

    private string GetInstalledDynamicRouteNextHop(string interfaceAlias, string destinationPrefix)
    {
        if (string.IsNullOrWhiteSpace(interfaceAlias) || string.IsNullOrWhiteSpace(destinationPrefix))
            return "";

        string safeAlias = ProcessRunner.EscapePsSingleQuoted(interfaceAlias);
        string safePrefix = ProcessRunner.EscapePsSingleQuoted(destinationPrefix);
        string script =
            $"$r = Get-NetRoute -AddressFamily IPv4 -InterfaceAlias '{safeAlias}' " +
            $"-DestinationPrefix '{safePrefix}' -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.NextHop -and $_.NextHop -ne '0.0.0.0' } | " +
            "Sort-Object RouteMetric | Select-Object -First 1; " +
            "if($r){ $r.NextHop }";

        string output = RunPowerShell(script, out _, 15000).Trim();
        return IPAddress.TryParse(output, out var hop) &&
               hop.AddressFamily == AddressFamily.InterNetwork
            ? hop.ToString()
            : "";
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

    public RouteCommandResult SynchronizeDynamicRoute(RouteEntry route, bool waitForGateway = true)
    {
        if (!ValidateRoute(route, out string validationError))
            return new RouteCommandResult { Success = false, Message = validationError };

        if (!route.IsDynamicGateway)
            return new RouteCommandResult { Success = false, Message = "该路由不是动态 DHCP 网关路由。" };

        if (!IsAdministrator())
            return new RouteCommandResult { Success = false, Message = "创建、同步动态路由需要以管理员身份运行 WinNetManager。" };

        string script = BuildDynamicSyncScript(route, waitForGateway ? 8 : 1);
        string output = ProcessRunner.RunPowerShell(
            script,
            out string error,
            out int exitCode,
            waitForGateway ? 30000 : 15000);

        if (exitCode != 0 || HasRealPowerShellError(error))
        {
            string msg = (output + Environment.NewLine + error).Trim();
            if (string.IsNullOrWhiteSpace(msg))
                msg = $"动态路由同步失败（退出码 {exitCode}）。";
            return new RouteCommandResult { Success = false, Message = msg };
        }

        string syncedLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(x => x.StartsWith("SYNCED|", StringComparison.Ordinal)) ?? "";
        if (!string.IsNullOrWhiteSpace(syncedLine))
        {
            string gateway = syncedLine["SYNCED|".Length..].Trim();
            route.NextHop = gateway;
            route.DynamicCurrentGateway = gateway;
            route.DynamicTaskStatus = "已注册/启用";
            route.Store = "DynamicTask+PersistentStore";
            return new RouteCommandResult
            {
                Success = true,
                Message = $"已将 {route.DestinationPrefix} 同步到 {route.InterfaceAlias} 当前网关 {gateway}。"
            };
        }

        if (output.IndexOf("NO_GATEWAY", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            route.DynamicCurrentGateway = "";
            route.DynamicTaskStatus = "已注册/启用";
            route.Store = "DynamicTask";
            return new RouteCommandResult
            {
                Success = true,
                Message = "计划任务已生效，但当前网卡尚未取得可用 IPv4 默认网关；本次未下发路由，将等待 DHCP/网络连接事件后自动重试。"
            };
        }

        return new RouteCommandResult
        {
            Success = false,
            Message = string.IsNullOrWhiteSpace(output)
                ? "动态路由同步没有返回预期结果。"
                : output.Trim()
        };
    }

    private RouteCommandResult AddDynamicRoute(RouteEntry route)
    {
        if (!IsAdministrator())
            return new RouteCommandResult { Success = false, Message = "创建动态 DHCP 网关路由需要以管理员身份运行 WinNetManager。" };

        var taskResult = RegisterDynamicRouteTask(route);
        if (!taskResult.Success)
            return taskResult;

        var syncResult = SynchronizeDynamicRoute(route, waitForGateway: false);
        if (syncResult.Success)
        {
            route.IsDynamicGateway = true;
            route.DynamicTaskStatus = "已注册/启用";
            return syncResult;
        }

        // A task that cannot execute the same sync logic would only create a hidden,
        // repeatedly failing background job. Roll it back on a real apply error.
        _ = DeleteDynamicRouteTask(route);
        return new RouteCommandResult
        {
            Success = false,
            Message = $"计划任务已回滚，因为首次同步失败：{syncResult.Message}"
        };
    }

    private RouteCommandResult DeleteDynamicRoute(RouteEntry route)
    {
        if (!IsAdministrator())
            return new RouteCommandResult { Success = false, Message = "删除动态 DHCP 网关路由需要以管理员身份运行 WinNetManager。" };

        // Delete the trigger first so a DHCP event cannot recreate the route while
        // we are removing its current PersistentStore/ActiveStore entries.
        var taskResult = DeleteDynamicRouteTask(route);
        if (!taskResult.Success)
            return taskResult;

        string safeAlias = ProcessRunner.EscapePsSingleQuoted(route.InterfaceAlias);
        string safePrefix = ProcessRunner.EscapePsSingleQuoted(route.DestinationPrefix);

        string script =
            $"$alias='{safeAlias}'; $prefix='{safePrefix}'; " +
            "Get-NetRoute -PolicyStore PersistentStore -AddressFamily IPv4 -InterfaceAlias $alias -DestinationPrefix $prefix -ErrorAction SilentlyContinue | " +
            "Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; " +
            "Get-NetRoute -PolicyStore ActiveStore -AddressFamily IPv4 -InterfaceAlias $alias -DestinationPrefix $prefix -ErrorAction SilentlyContinue | " +
            "Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue";

        _ = ProcessRunner.RunPowerShell(script, out string error, out int exitCode, 20000);
        if (exitCode != 0 || HasRealPowerShellError(error))
        {
            return new RouteCommandResult
            {
                Success = false,
                Message = $"计划任务已删除，但当前路由项清理失败：{error.Trim()}"
            };
        }

        return new RouteCommandResult
        {
            Success = true,
            Message = $"已删除动态路由及计划任务 {route.DynamicTriggerTaskName}。"
        };
    }

    private RouteCommandResult RegisterDynamicRouteTask(RouteEntry route)
    {
        XDocument taskXml = BuildDynamicTaskXml(route);
        string tempXml = Path.Combine(
            Path.GetTempPath(),
            $"WinNetManager-DynamicRoute-{Guid.NewGuid():N}.xml");

        try
        {
            using (var writer = new StreamWriter(tempXml, false, Encoding.Unicode))
                taskXml.Save(writer);

            string args = $"/Create /TN \"{route.DynamicTriggerTaskName}\" /XML \"{tempXml}\" /RU SYSTEM /F";
            string output = ProcessRunner.RunNative(
                "schtasks.exe",
                args,
                out string error,
                out int exitCode,
                20000);

            if (exitCode == 0)
            {
                route.DynamicTaskStatus = "已注册/启用";
                return new RouteCommandResult
                {
                    Success = true,
                    Message = $"已注册计划任务 {route.DynamicTriggerTaskName}。"
                };
            }

            return new RouteCommandResult
            {
                Success = false,
                Message = FriendlyTaskSchedulerError(output, error, exitCode)
            };
        }
        finally
        {
            try { if (File.Exists(tempXml)) File.Delete(tempXml); } catch { }
        }
    }

    internal static XDocument BuildDynamicTaskXml(RouteEntry route)
    {
        var metadata = new DynamicRouteTaskMetadata
        {
            AddressFamily = route.AddressFamily,
            DestinationPrefix = route.DestinationPrefix,
            InterfaceAlias = route.InterfaceAlias,
            RouteMetric = route.RouteMetric
        };

        string metadataJson = JsonSerializer.Serialize(metadata);
        string description = DynamicDescriptionPrefix +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(metadataJson));

        string syncScript = BuildDynamicSyncScript(route, 10);
        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(syncScript));

        const string subscription =
            "<QueryList>" +
            "<Query Id=\"0\" Path=\"Microsoft-Windows-Dhcp-Client/Operational\">" +
            "<Select Path=\"Microsoft-Windows-Dhcp-Client/Operational\">*[System[(EventID=50036 or EventID=50047)]]</Select>" +
            "</Query>" +
            "<Query Id=\"1\" Path=\"Microsoft-Windows-NetworkProfile/Operational\">" +
            "<Select Path=\"Microsoft-Windows-NetworkProfile/Operational\">*[System[(EventID=10000)]]</Select>" +
            "</Query>" +
            "</QueryList>";

        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return new XDocument(
            new XDeclaration("1.0", "utf-16", null),
            new XElement(ns + "Task",
                new XAttribute("version", "1.4"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Author", "WinNetManager"),
                    new XElement(ns + "Description", description),
                    new XElement(ns + "URI", "\\" + route.DynamicTriggerTaskName)),
                new XElement(ns + "Triggers",
                    // Boot trigger is a safety net. Event triggers remain the primary
                    // mechanism, so WinNetManager itself never needs to stay resident.
                    new XElement(ns + "BootTrigger",
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "Delay", "PT15S")),
                    new XElement(ns + "EventTrigger",
                        new XElement(ns + "Enabled", "true"),
                        // eventTriggerType requires Subscription before Delay.
                        new XElement(ns + "Subscription", subscription),
                        new XElement(ns + "Delay", "PT3S"))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", "S-1-5-18"),
                        new XElement(ns + "RunLevel", "HighestAvailable"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(ns + "IdleSettings",
                        new XElement(ns + "StopOnIdleEnd", "false"),
                        new XElement(ns + "RestartOnIdle", "false")),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "RunOnlyIfIdle", "false"),
                    new XElement(ns + "WakeToRun", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT2M"),
                    new XElement(ns + "Priority", "7")),
                new XElement(ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", "powershell.exe"),
                        new XElement(ns + "Arguments",
                            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encodedCommand}")))));
    }

    private RouteCommandResult DeleteDynamicRouteTask(RouteEntry route)
    {
        string taskName = route.DynamicTriggerTaskName;
        if (!DynamicTaskExists(taskName))
            return new RouteCommandResult { Success = true, Message = "对应计划任务已不存在。" };

        string output = ProcessRunner.RunNative(
            "schtasks.exe",
            $"/Delete /TN \"{taskName}\" /F",
            out string error,
            out int exitCode,
            15000);

        return exitCode == 0
            ? new RouteCommandResult { Success = true, Message = $"已删除计划任务 {taskName}。" }
            : new RouteCommandResult
            {
                Success = false,
                Message = FriendlyTaskSchedulerError(output, error, exitCode)
            };
    }

    private static bool DynamicTaskExists(string taskName)
    {
        _ = ProcessRunner.RunNative(
            "schtasks.exe",
            $"/Query /TN \"{taskName.Replace("\"", "")}\"",
            out _,
            out int exitCode,
            10000);
        return exitCode == 0;
    }

    internal static string BuildDynamicSyncScript(RouteEntry route, int attempts)
    {
        string safeAlias = ProcessRunner.EscapePsSingleQuoted(route.InterfaceAlias);
        string safePrefix = ProcessRunner.EscapePsSingleQuoted(route.DestinationPrefix);
        int safeAttempts = Math.Clamp(attempts, 1, 30);
        string metric = int.TryParse(route.RouteMetric, out int parsedMetric)
            ? Math.Clamp(parsedMetric, 0, 999999).ToString()
            : "1";

        return
            "$ErrorActionPreference='Stop'; " +
            $"$alias='{safeAlias}'; $prefix='{safePrefix}'; $metric={metric}; $gw=$null; " +
            $"for($i=0; $i -lt {safeAttempts} -and -not $gw; $i++){{ " +
            "$gw = Get-NetRoute -PolicyStore ActiveStore -AddressFamily IPv4 -InterfaceAlias $alias -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.NextHop -and $_.NextHop -ne '0.0.0.0' } | Sort-Object RouteMetric | Select-Object -First 1 -ExpandProperty NextHop; " +
            $"if(-not $gw -and $i -lt {safeAttempts - 1}){{ Start-Sleep -Seconds 2 }} }}; " +
            "if(-not $gw){ Write-Output 'NO_GATEWAY'; exit 0 }; " +
            "Get-NetRoute -PolicyStore PersistentStore -AddressFamily IPv4 -InterfaceAlias $alias -DestinationPrefix $prefix -ErrorAction SilentlyContinue | " +
            "Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; " +
            "Get-NetRoute -PolicyStore ActiveStore -AddressFamily IPv4 -InterfaceAlias $alias -DestinationPrefix $prefix -ErrorAction SilentlyContinue | " +
            "Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; " +
            "New-NetRoute -PolicyStore PersistentStore -AddressFamily IPv4 -InterfaceAlias $alias -DestinationPrefix $prefix -NextHop $gw -RouteMetric $metric -Confirm:$false -ErrorAction Stop | Out-Null; " +
            "Write-Output ('SYNCED|' + $gw)";
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasRealPowerShellError(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        error.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) < 0 &&
        error.IndexOf("警告", StringComparison.OrdinalIgnoreCase) < 0;

    private static string FriendlyTaskSchedulerError(string output, string error, int exitCode)
    {
        string msg = (output + Environment.NewLine + error).Trim();
        if (ContainsIgnoreCase(msg, "Access is denied") ||
            ContainsIgnoreCase(msg, "拒绝访问") ||
            ContainsIgnoreCase(msg, "需要提升的权限"))
            return "任务计划程序拒绝访问。请以管理员身份运行 WinNetManager。";

        return string.IsNullOrWhiteSpace(msg)
            ? $"任务计划程序操作失败（退出码 {exitCode}）。"
            : msg;
    }

    public RouteCommandResult AddRoute(RouteEntry route)
    {
        if (!ValidateRoute(route, out string validationError))
            return new RouteCommandResult { Success = false, Message = validationError };

        if (route.IsDynamicGateway)
            return AddDynamicRoute(route);

        string prefix = route.DestinationPrefix ?? "";
        string safeAlias = ProcessRunner.EscapePsSingleQuoted(route.InterfaceAlias);
        string safeHop = ProcessRunner.EscapePsSingleQuoted(route.NextHop);
        string metric = route.RouteMetric ?? "1";

        string addCmd;
        string setCmd;
        if (route.AddressFamily == "IPv6")
        {
            addCmd = $"netsh interface ipv6 add route prefix={prefix} interface='{safeAlias}' nexthop={safeHop} metric={metric} store=persistent";
            setCmd = $"netsh interface ipv6 set route prefix={prefix} interface='{safeAlias}' nexthop={safeHop} metric={metric} store=persistent";
        }
        else
        {
            addCmd = $"netsh interface ipv4 add route {prefix} interface='{safeAlias}' nexthop={safeHop} metric={metric} store=persistent";
            setCmd = $"netsh interface ipv4 set route prefix={prefix} interface='{safeAlias}' nexthop={safeHop} metric={metric} store=persistent";
        }

        var add = ExecuteNetsh(addCmd);
        if (add.Success)
            return add;

        if (ContainsIgnoreCase(add.Message, "already exists")
            || ContainsIgnoreCase(add.Message, "已存在"))
        {
            var set = ExecuteNetsh(setCmd);
            if (set.Success)
                return new RouteCommandResult { Success = true, Message = "路由已在运行表中（常见于 IPv6 路由器通告），已改为更新度量并写入持久存储。" };
        }

        return add;
    }

    public RouteCommandResult DeleteRoute(RouteEntry route)
    {
        if (!ValidateRoute(route, out string validationError))
            return new RouteCommandResult { Success = false, Message = validationError };

        if (route.IsDynamicGateway)
            return DeleteDynamicRoute(route);

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
