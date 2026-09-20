using System;
using System.Collections.Generic;
using System.Text;
using WinNetManager.Models;

namespace WinNetManager.Services;

public class MetricResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public class InterfaceMetricManager
{
    public List<InterfaceMetricInfo> GetMetrics()
    {
        var metrics = new List<InterfaceMetricInfo>();

        string script =
            "Get-NetIPInterface | " +
            "Select-Object InterfaceAlias, AddressFamily, InterfaceIndex, AutomaticMetric, InterfaceMetric, WeakHostSend, WeakHostReceive, IgnoreDefaultRoutes, RouterDiscovery | " +
            "ConvertTo-Csv -NoTypeInformation";

        string error;
        string output = ProcessRunner.RunPowerShell(script, out error, 15000);

        if (!string.IsNullOrEmpty(error) && !CI(error, "警告") && !CI(error, "Warning"))
            return metrics;

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return metrics;

        string[] headers = ParseCsvLine(lines[0]);
        int idxAlias = Array.IndexOf(headers, "InterfaceAlias");
        int idxFamily = Array.IndexOf(headers, "AddressFamily");
        int idxIndex = Array.IndexOf(headers, "InterfaceIndex");
        int idxAuto = Array.IndexOf(headers, "AutomaticMetric");
        int idxMetric = Array.IndexOf(headers, "InterfaceMetric");
        int idxWeakSend = Array.IndexOf(headers, "WeakHostSend");
        int idxWeakRecv = Array.IndexOf(headers, "WeakHostReceive");
        int idxIgnore = Array.IndexOf(headers, "IgnoreDefaultRoutes");
        int idxRouter = Array.IndexOf(headers, "RouterDiscovery");

        for (int i = 1; i < lines.Length; i++)
        {
            string[] values = ParseCsvLine(lines[i]);
            if (values.Length < 2) continue;

            string family = idxFamily >= 0 && idxFamily < values.Length ? values[idxFamily] : "";
            // AddressFamily: 2 = IPv4, 23 = IPv6
            string familyName = family == "2" ? "IPv4" : (family == "23" ? "IPv6" : family);

            string autoStr = idxAuto >= 0 && idxAuto < values.Length ? values[idxAuto] : "";
            bool autoMetric = autoStr == "True" || autoStr == "1" || autoStr.Equals("Enabled", StringComparison.OrdinalIgnoreCase);

            string metricStr = idxMetric >= 0 && idxMetric < values.Length ? values[idxMetric] : "";
            int.TryParse(metricStr, out int metric);

            string weakSend = idxWeakSend >= 0 && idxWeakSend < values.Length ? values[idxWeakSend] : "";
            string weakRecv = idxWeakRecv >= 0 && idxWeakRecv < values.Length ? values[idxWeakRecv] : "";

            metrics.Add(new InterfaceMetricInfo
            {
                InterfaceAlias = idxAlias >= 0 && idxAlias < values.Length ? values[idxAlias] : "",
                AddressFamily = familyName,
                InterfaceIndex = idxIndex >= 0 && idxIndex < values.Length ? values[idxIndex] : "",
                AutomaticMetric = autoMetric,
                InterfaceMetric = metric,
                WeakHostSend = IsOn(weakSend),
                WeakHostReceive = IsOn(weakRecv),
                IgnoreDefaultRoutes = idxIgnore >= 0 && idxIgnore < values.Length && IsOn(values[idxIgnore]),
                RouterDiscovery = idxRouter >= 0 && idxRouter < values.Length ? values[idxRouter] : ""
            });
        }

        return metrics;
    }

    public MetricResult SetMetric(string interfaceAlias, string addressFamily, int metric)
    {
        string family = addressFamily == "IPv6" ? "IPv6" : "IPv4";
        string script =
            $"Set-NetIPInterface " +
            $"-InterfaceAlias '{ProcessRunner.EscapePsSingleQuoted(interfaceAlias)}' " +
            $"-AddressFamily {family} " +
            $"-InterfaceMetric {metric}";

        return ExecutePowerShell(script);
    }

    public MetricResult SetAutoMetric(string interfaceAlias, string addressFamily)
    {
        string family = addressFamily == "IPv6" ? "IPv6" : "IPv4";
        string script =
            $"Set-NetIPInterface " +
            $"-InterfaceAlias '{ProcessRunner.EscapePsSingleQuoted(interfaceAlias)}' " +
            $"-AddressFamily {family} " +
            $"-AutomaticMetric $true";

        return ExecutePowerShell(script);
    }

    public static string GetSetMetricCommandPreview(string interfaceAlias, string addressFamily, int metric)
    {
        string family = addressFamily == "IPv6" ? "IPv6" : "IPv4";
        string safeAlias = ProcessRunner.EscapePsSingleQuoted(interfaceAlias);
        return $"Set-NetIPInterface -InterfaceAlias '{safeAlias}' -AddressFamily {family} -InterfaceMetric {metric}";
    }

    public static string GetSetAutoMetricCommandPreview(string interfaceAlias, string addressFamily)
    {
        string family = addressFamily == "IPv6" ? "IPv6" : "IPv4";
        string safeAlias = ProcessRunner.EscapePsSingleQuoted(interfaceAlias);
        return $"Set-NetIPInterface -InterfaceAlias '{safeAlias}' -AddressFamily {family} -AutomaticMetric $true";
    }

    public List<GatewayMetricInfo> GetGatewayMetrics()
    {
        var metrics = new List<GatewayMetricInfo>();

        string script =
            "Get-NetRoute -DestinationPrefix '0.0.0.0/0', '::/0' | " +
            "Select-Object InterfaceAlias, InterfaceIndex, AddressFamily, NextHop, RouteMetric | " +
            "ConvertTo-Csv -NoTypeInformation";

        string error;
        string output = ProcessRunner.RunPowerShell(script, out error, 15000);

        if (!string.IsNullOrEmpty(error) && !CI(error, "警告") && !CI(error, "Warning"))
            return metrics;

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return metrics;

        string[] headers = ParseCsvLine(lines[0]);
        int idxAlias = Array.IndexOf(headers, "InterfaceAlias");
        int idxIndex = Array.IndexOf(headers, "InterfaceIndex");
        int idxFamily = Array.IndexOf(headers, "AddressFamily");
        int idxHop = Array.IndexOf(headers, "NextHop");
        int idxMetric = Array.IndexOf(headers, "RouteMetric");

        for (int i = 1; i < lines.Length; i++)
        {
            string[] values = ParseCsvLine(lines[i]);
            if (values.Length < 2) continue;

            string family = idxFamily >= 0 && idxFamily < values.Length ? values[idxFamily] : "";
            string familyName = family == "2" ? "IPv4" : (family == "23" ? "IPv6" : family);

            metrics.Add(new GatewayMetricInfo
            {
                InterfaceAlias = idxAlias >= 0 && idxAlias < values.Length ? values[idxAlias] : "",
                InterfaceIndex = idxIndex >= 0 && idxIndex < values.Length ? values[idxIndex] : "",
                AddressFamily = familyName,
                NextHop = idxHop >= 0 && idxHop < values.Length ? values[idxHop] : "",
                RouteMetric = idxMetric >= 0 && idxMetric < values.Length ? values[idxMetric] : ""
            });
        }

        return metrics;
    }

    public MetricResult SetGatewayMetric(string interfaceAlias, string addressFamily, string nextHop, int metric)
    {
        string prefix = addressFamily == "IPv6" ? "::/0" : "0.0.0.0/0";
        string family = addressFamily == "IPv6" ? "IPv6" : "IPv4";
        string safeAlias = ProcessRunner.EscapePsSingleQuoted(interfaceAlias);
        string safeHop = ProcessRunner.EscapePsSingleQuoted(nextHop);

        string script = $"Set-NetRoute -AddressFamily {family} -DestinationPrefix '{prefix}' -InterfaceAlias '{safeAlias}' -NextHop '{safeHop}' -RouteMetric {metric}";
        return ExecutePowerShell(script);
    }

    public static string GetSetGatewayMetricCommandPreview(string interfaceAlias, string addressFamily, string nextHop, int metric)
    {
        string prefix = addressFamily == "IPv6" ? "::/0" : "0.0.0.0/0";
        string family = addressFamily == "IPv6" ? "IPv6" : "IPv4";
        string safeAlias = ProcessRunner.EscapePsSingleQuoted(interfaceAlias);
        string safeHop = ProcessRunner.EscapePsSingleQuoted(nextHop);
        return $"Set-NetRoute -AddressFamily {family} -DestinationPrefix '{prefix}' -InterfaceAlias '{safeAlias}' -NextHop '{safeHop}' -RouteMetric {metric}";
    }

    public MetricResult SetIgnoreDefaultRoutes(string interfaceAlias, string addressFamily, bool ignore)
    {
        string family = addressFamily == "IPv6" ? "IPv6" : "IPv4";
        string flag = ignore ? "Enabled" : "Disabled";
        string script =
            $"Set-NetIPInterface " +
            $"-InterfaceAlias '{ProcessRunner.EscapePsSingleQuoted(interfaceAlias)}' " +
            $"-AddressFamily {family} " +
            $"-IgnoreDefaultRoutes {flag}";
        return ExecutePowerShell(script);
    }

    public static string GetSetIgnoreDefaultRoutesCommandPreview(string interfaceAlias, string addressFamily, bool ignore)
    {
        string family = addressFamily == "IPv6" ? "IPv6" : "IPv4";
        string flag = ignore ? "Enabled" : "Disabled";
        return $"Set-NetIPInterface -InterfaceAlias '{ProcessRunner.EscapePsSingleQuoted(interfaceAlias)}' -AddressFamily {family} -IgnoreDefaultRoutes {flag}";
    }

    public MetricResult SetRouterDiscovery(string interfaceAlias, string addressFamily, bool enabled)
    {
        string family = addressFamily == "IPv6" ? "IPv6" : "IPv4";
        string flag = enabled ? "Enabled" : "Disabled";
        string script =
            $"Set-NetIPInterface " +
            $"-InterfaceAlias '{ProcessRunner.EscapePsSingleQuoted(interfaceAlias)}' " +
            $"-AddressFamily {family} " +
            $"-RouterDiscovery {flag}";
        return ExecutePowerShell(script);
    }

    public static string GetSetRouterDiscoveryCommandPreview(string interfaceAlias, string addressFamily, bool enabled)
    {
        string family = addressFamily == "IPv6" ? "IPv6" : "IPv4";
        string flag = enabled ? "Enabled" : "Disabled";
        return $"Set-NetIPInterface -InterfaceAlias '{ProcessRunner.EscapePsSingleQuoted(interfaceAlias)}' -AddressFamily {family} -RouterDiscovery {flag}";
    }

    public MetricResult PinIpv6Default(string interfaceAlias, string nextHop, int metric)
    {
        // Keep RouterDiscovery enabled so SLAAC can still learn/renew addresses from RA.
        // IgnoreDefaultRoutes must be Disabled, otherwise even the static ::/0 is not used for ping -S.
        var unignore = SetIgnoreDefaultRoutes(interfaceAlias, "IPv6", ignore: false);
        if (!unignore.Success)
            return unignore;
        var add = new RoutingManager().AddRoute(new RouteEntry
        {
            AddressFamily = "IPv6",
            DestinationPrefix = "::/0",
            NextHop = nextHop,
            InterfaceAlias = interfaceAlias,
            RouteMetric = metric.ToString(),
            Store = "PersistentStore",
        });
        if (!add.Success)
            return new MetricResult { Success = false, Message = add.Message };
        return new MetricResult { Success = true, Message = add.Message };
    }

    public static string GetPinIpv6DefaultCommandPreview(string interfaceAlias, string nextHop, int metric)
    {
        return GetSetIgnoreDefaultRoutesCommandPreview(interfaceAlias, "IPv6", ignore: false)
            + "\n" + GetSetRouterDiscoveryCommandPreview(interfaceAlias, "IPv6", enabled: false)
            + "\n" + $"netsh interface ipv6 add route prefix=::/0 interface='{ProcessRunner.EscapePsSingleQuoted(interfaceAlias)}' nexthop={ProcessRunner.EscapePsSingleQuoted(nextHop)} metric={metric} store=persistent";
    }

    private static MetricResult ExecutePowerShell(string script)
    {
        string error;
        string output = ProcessRunner.RunPowerShell(script, out error, 15000);

        if (!string.IsNullOrEmpty(error) && !CI(error, "警告") && !CI(error, "Warning"))
        {
            string msg = error.Trim();
            if (CI(msg, "requires elevation") || CI(msg, "Access is denied") || CI(msg, "拒绝访问"))
                msg = "错误：需要以管理员身份运行本程序。";
            else if (CI(msg, "not found") || CI(msg, "找不到"))
                msg = "错误：找不到指定的网络接口。";
            return new MetricResult { Success = false, Message = msg };
        }

        return new MetricResult { Success = true, Message = output };
    }

    private static string[] ParseCsvLine(string line) => CsvParser.ParseLine(line);

    public MetricResult SetWeakHost(string interfaceAlias, string addressFamily, bool enabled)
    {
        string family = addressFamily == "IPv6" ? "IPv6" : "IPv4";
        string flag = enabled ? "Enabled" : "Disabled";
        string script =
            $"Set-NetIPInterface " +
            $"-InterfaceAlias '{ProcessRunner.EscapePsSingleQuoted(interfaceAlias)}' " +
            $"-AddressFamily {family} " +
            $"-WeakHostSend {flag} " +
            $"-WeakHostReceive {flag}";
        return ExecutePowerShell(script);
    }

    public static string GetSetWeakHostCommandPreview(string interfaceAlias, string addressFamily, bool enabled)
    {
        string family = addressFamily == "IPv6" ? "IPv6" : "IPv4";
        string flag = enabled ? "Enabled" : "Disabled";
        string safeAlias = ProcessRunner.EscapePsSingleQuoted(interfaceAlias);
        return $"Set-NetIPInterface -InterfaceAlias '{safeAlias}' -AddressFamily {family} -WeakHostSend {flag} -WeakHostReceive {flag}";
    }

    private static bool IsOn(string value)
        => value == "1"
            || value.Equals("True", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Enabled", StringComparison.OrdinalIgnoreCase);

    private static bool CI(string source, string value)
        => source?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
}
