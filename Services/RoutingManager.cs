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
        string family = route.AddressFamily == "IPv6" ? "IPv6" : "IPv4";
        string script =
            $"New-NetRoute -AddressFamily {family} " +
            $"-DestinationPrefix '{ProcessRunner.EscapePsSingleQuoted(route.DestinationPrefix)}' " +
            $"-InterfaceAlias '{ProcessRunner.EscapePsSingleQuoted(route.InterfaceAlias)}' " +
            $"-NextHop '{ProcessRunner.EscapePsSingleQuoted(route.NextHop)}' " +
            $"-RouteMetric {route.RouteMetric} " +
            $"-PolicyStore PersistentStore";

        return ExecuteWritePowerShell(script);
    }

    public RouteCommandResult DeleteRoute(RouteEntry route)
    {
        string family = route.AddressFamily == "IPv6" ? "IPv6" : "IPv4";
        string script =
            $"Remove-NetRoute -AddressFamily {family} " +
            $"-DestinationPrefix '{ProcessRunner.EscapePsSingleQuoted(route.DestinationPrefix)}' " +
            $"-InterfaceAlias '{ProcessRunner.EscapePsSingleQuoted(route.InterfaceAlias)}' " +
            $"-NextHop '{ProcessRunner.EscapePsSingleQuoted(route.NextHop)}' " +
            $"-RouteMetric {route.RouteMetric} " +
            $"-PolicyStore PersistentStore -Confirm:$false";

        return ExecuteWritePowerShell(script);
    }

    private RouteCommandResult ExecuteWritePowerShell(string script)
    {
        string error;
        string output = RunPowerShell(script, out error, timeoutMs: 30000);

        if (!string.IsNullOrEmpty(error) && !error.Contains("警告"))
        {
            string msg = error.Trim();
            if (msg.Contains("需要提升的权限") || msg.Contains("requires elevation") || msg.Contains("Access is denied"))
                msg = "需要以管理员身份运行本程序。";
            else if (msg.Contains("已存在") || msg.Contains("already exists"))
                msg = "该路由已存在。";

            return new RouteCommandResult { Success = false, Message = msg };
        }

        return new RouteCommandResult { Success = true, Message = output };
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
