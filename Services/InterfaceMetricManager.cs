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
            "Select-Object InterfaceAlias, AddressFamily, InterfaceIndex, AutomaticMetric, InterfaceMetric | " +
            "ConvertTo-Csv -NoTypeInformation";

        string error;
        string output = ProcessRunner.RunPowerShell(script, out error, 15000);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
            return metrics;

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return metrics;

        string[] headers = ParseCsvLine(lines[0]);
        int idxAlias = Array.IndexOf(headers, "InterfaceAlias");
        int idxFamily = Array.IndexOf(headers, "AddressFamily");
        int idxIndex = Array.IndexOf(headers, "InterfaceIndex");
        int idxAuto = Array.IndexOf(headers, "AutomaticMetric");
        int idxMetric = Array.IndexOf(headers, "InterfaceMetric");

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

            metrics.Add(new InterfaceMetricInfo
            {
                InterfaceAlias = idxAlias >= 0 && idxAlias < values.Length ? values[idxAlias] : "",
                AddressFamily = familyName,
                InterfaceIndex = idxIndex >= 0 && idxIndex < values.Length ? values[idxIndex] : "",
                AutomaticMetric = autoMetric,
                InterfaceMetric = metric
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
        return $"Set-NetIPInterface -InterfaceAlias '{interfaceAlias}' -AddressFamily {family} -InterfaceMetric {metric}";
    }

    public static string GetSetAutoMetricCommandPreview(string interfaceAlias, string addressFamily)
    {
        string family = addressFamily == "IPv6" ? "IPv6" : "IPv4";
        return $"Set-NetIPInterface -InterfaceAlias '{interfaceAlias}' -AddressFamily {family} -AutomaticMetric $true";
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

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
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
        string safeAlias = ProcessRunner.EscapePsSingleQuoted(interfaceAlias);
        string safeHop = ProcessRunner.EscapePsSingleQuoted(nextHop);

        string script = $"Set-NetRoute -DestinationPrefix '{prefix}' -InterfaceAlias '{safeAlias}' -NextHop '{safeHop}' -RouteMetric {metric}";
        return ExecutePowerShell(script);
    }

    public static string GetSetGatewayMetricCommandPreview(string interfaceAlias, string addressFamily, string nextHop, int metric)
    {
        string prefix = addressFamily == "IPv6" ? "::/0" : "0.0.0.0/0";
        return $"Set-NetRoute -DestinationPrefix '{prefix}' -InterfaceAlias '{interfaceAlias}' -NextHop '{nextHop}' -RouteMetric {metric}";
    }

    private static MetricResult ExecutePowerShell(string script)
    {
        string error;
        string output = ProcessRunner.RunPowerShell(script, out error, 15000);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
        {
            string msg = error.Trim();
            if (msg.Contains("requires elevation") || msg.Contains("Access is denied") || msg.Contains("拒绝访问"))
                msg = "错误：需要以管理员身份运行本程序。";
            else if (msg.Contains("not found") || msg.Contains("找不到"))
                msg = "错误：找不到指定的网络接口。";
            return new MetricResult { Success = false, Message = msg };
        }

        return new MetricResult { Success = true, Message = output };
    }

    private static string[] ParseCsvLine(string line)
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
