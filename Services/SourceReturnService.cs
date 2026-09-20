using System.Net;
using System.Text;
using WinNetManager.Core.Net;
using WinNetManager.Models;

namespace WinNetManager.Services;

public class SourceReturnService
{
    private readonly RoutingManager _routes = new();
    private readonly InterfaceMetricManager _metrics = new();
    private readonly FirewallManager _firewall = new();

    public List<ExistingDefaultRoute> GetExistingDefaults()
    {
        var list = new List<ExistingDefaultRoute>();
        foreach (var r in _routes.GetPersistentRoutes())
        {
            if (!SourceReturnPlanner.IsDefaultPrefix(r.DestinationPrefix, r.AddressFamily))
                continue;
            AddUnique(list, r.AddressFamily, r.DestinationPrefix, r.NextHop, r.InterfaceAlias, r.RouteMetric);
        }

        foreach (var g in _metrics.GetGatewayMetrics())
        {
            string dest = g.AddressFamily == "IPv6" ? "::/0" : "0.0.0.0/0";
            AddUnique(list, g.AddressFamily, dest, g.NextHop, g.InterfaceAlias, g.RouteMetric);
        }

        return list;
    }

    public List<RouteEntry> GetPersistentRoutes() => _routes.GetPersistentRoutes();

    public List<NetInterface> GetInterfaces() => _routes.GetInterfaces();

    public List<NetworkAdapterInfo> GetAdapters() => new DhcpManager().GetAdapters();

    public (List<string> Successes, List<string> Errors) Apply(IReadOnlyList<SourceReturnAction> actions)
    {
        var oks = new List<string>();
        var errors = new List<string>();
        foreach (var action in actions)
        {
            try
            {
                switch (action.Kind)
                {
                    case "AddRoute":
                    {
                        var res = _routes.AddRoute(ToRoute(action));
                        if (res.Success) oks.Add("新增路由 " + DescribeRoute(action));
                        else errors.Add("新增路由失败 " + DescribeRoute(action) + "\uff1a" + res.Message);
                        break;
                    }
                    case "DeleteRoute":
                    {
                        var res = _routes.DeleteRoute(ToRoute(action, useExisting: true));
                        if (res.Success) oks.Add("删除本接口旧默认路由 " + DescribeRoute(action));
                        else errors.Add("删除本接口旧默认路由失败 " + DescribeRoute(action) + "\uff1a" + res.Message);
                        break;
                    }
                    case "ReplaceRoute":
                    {
                        var original = ToRoute(action, useExisting: true);
                        var updated = ToRoute(action);
                        var del = _routes.DeleteRoute(original);
                        if (!del.Success)
                        {
                            errors.Add("更新路由失败 " + DescribeRoute(action) + "\uff1a" + del.Message);
                            break;
                        }
                        var add = _routes.AddRoute(updated);
                        if (add.Success)
                        {
                            oks.Add("更新路由跃点 " + DescribeRoute(action));
                        }
                        else
                        {
                            _routes.AddRoute(original);
                            errors.Add("更新路由失败 " + DescribeRoute(action) + "\uff1a" + add.Message);
                        }
                        break;
                    }
                    case "SetStrongHost":
                    {
                        var r4 = _metrics.SetWeakHost(action.InterfaceAlias, "IPv4", enabled: false);
                        var r6 = _metrics.SetWeakHost(action.InterfaceAlias, "IPv6", enabled: false);
                        if (r4.Success && r6.Success)
                            oks.Add("已将该网卡设为强主机 " + action.InterfaceAlias);
                        else
                        {
                            if (!r4.Success) errors.Add("强主机设置失败 " + action.InterfaceAlias + " IPv4\uff1a" + r4.Message);
                            if (!r6.Success) errors.Add("强主机设置失败 " + action.InterfaceAlias + " IPv6\uff1a" + r6.Message);
                        }
                        break;
                    }
                    case "AddFirewall":
                    {
                        var rule = new FirewallRuleInfo
                        {
                            DisplayName = string.IsNullOrWhiteSpace(action.NextHop)
                                ? $"WinNetManager_Inbound_{action.InterfaceAlias}_TCP"
                                : action.NextHop,
                            Direction = "Inbound",
                            Action = "Allow",
                            Enabled = "True",
                            Profile = "Any",
                            Protocol = "TCP",
                            LocalPort = action.Detail ?? "",
                            InterfaceAlias = action.InterfaceAlias,
                        };
                        var res = _firewall.AddRule(rule);
                        if (res.Success) oks.Add("已添加入站防火墙规则 " + rule.DisplayName);
                        else errors.Add("防火墙规则失败 " + rule.DisplayName + "\uff1a" + res.Message);
                        break;
                    }
                    default:
                        errors.Add("未知动作 " + action.Kind);
                        break;
                }
            }
            catch (Exception ex)
            {
                errors.Add(action.Kind + "\uff1a" + ex.Message);
            }
        }
        return (oks, errors);
    }

    public static string BuildPreview(IReadOnlyList<SourceReturnAction> actions)
    {
        var sb = new StringBuilder();
        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case "AddRoute":
                    sb.AppendLine(RouteCommand(action, isDelete: false));
                    break;
                case "DeleteRoute":
                    sb.AppendLine(RouteCommand(action, isDelete: true, useExisting: true));
                    break;
                case "ReplaceRoute":
                    sb.AppendLine(RouteCommand(action, isDelete: true, useExisting: true));
                    sb.AppendLine(RouteCommand(action, isDelete: false));
                    break;
                case "SetStrongHost":
                    sb.AppendLine(InterfaceMetricManager.GetSetWeakHostCommandPreview(action.InterfaceAlias, "IPv4", enabled: false));
                    sb.AppendLine(InterfaceMetricManager.GetSetWeakHostCommandPreview(action.InterfaceAlias, "IPv6", enabled: false));
                    break;
                case "AddFirewall":
                    var rule = new FirewallRuleInfo
                    {
                        DisplayName = string.IsNullOrWhiteSpace(action.NextHop)
                            ? $"WinNetManager_Inbound_{action.InterfaceAlias}_TCP"
                            : action.NextHop,
                        Direction = "Inbound",
                        Action = "Allow",
                        Profile = "Any",
                        Protocol = "TCP",
                        LocalPort = action.Detail ?? "",
                        InterfaceAlias = action.InterfaceAlias,
                    };
                    sb.AppendLine(FirewallManager.GetAddCommandPreview(rule));
                    break;
            }
        }
        return sb.ToString().Trim();
    }

    public static string? GuessIpv4Gateway(string? ipv4)
    {
        if (string.IsNullOrWhiteSpace(ipv4)) return null;
        string first = ipv4.Split(',')[0].Trim();
        if (!IPAddress.TryParse(first, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return null;
        byte[] bytes = ip.GetAddressBytes();
        bytes[3] = 1;
        return new IPAddress(bytes).ToString();
    }

    private static void AddUnique(
        List<ExistingDefaultRoute> list,
        string family, string dest, string hop, string alias, string metric)
    {
        if (list.Any(r =>
            r.AddressFamily.Equals(family, StringComparison.OrdinalIgnoreCase)
            && r.DestinationPrefix.Equals(dest, StringComparison.OrdinalIgnoreCase)
            && r.NextHop.Equals(hop, StringComparison.OrdinalIgnoreCase)
            && r.InterfaceAlias.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            return;

        list.Add(new ExistingDefaultRoute
        {
            AddressFamily = family,
            DestinationPrefix = dest,
            NextHop = hop,
            InterfaceAlias = alias,
            RouteMetric = metric,
        });
    }

    private static RouteEntry ToRoute(SourceReturnAction action, bool useExisting = false)
    {
        return new RouteEntry
        {
            AddressFamily = action.AddressFamily,
            DestinationPrefix = action.DestinationPrefix,
            NextHop = useExisting && !string.IsNullOrWhiteSpace(action.ExistingNextHop)
                ? action.ExistingNextHop
                : action.NextHop,
            InterfaceAlias = action.InterfaceAlias,
            RouteMetric = useExisting && !string.IsNullOrWhiteSpace(action.ExistingMetric)
                ? action.ExistingMetric
                : action.Metric,
            Store = "PersistentStore",
        };
    }

    private static string DescribeRoute(SourceReturnAction action)
        => $"{action.AddressFamily} {action.DestinationPrefix} via {action.NextHop} ({action.InterfaceAlias})";

    private static string RouteCommand(SourceReturnAction action, bool isDelete, bool useExisting = false)
    {
        var route = ToRoute(action, useExisting);
        string safeAlias = ProcessRunner.EscapePsSingleQuoted(route.InterfaceAlias);
        string safeHop = ProcessRunner.EscapePsSingleQuoted(route.NextHop);
        string prefix = route.DestinationPrefix ?? "";
        if (route.AddressFamily == "IPv6")
        {
            if (isDelete)
                return $"netsh interface ipv6 delete route prefix={prefix} interface='{safeAlias}' nexthop={safeHop} store=persistent";
            return $"netsh interface ipv6 add route prefix={prefix} interface='{safeAlias}' nexthop={safeHop} metric={route.RouteMetric} store=persistent";
        }
        if (isDelete)
            return $"netsh interface ipv4 delete route {prefix} interface='{safeAlias}' nexthop={safeHop} store=persistent";
        return $"netsh interface ipv4 add route {prefix} interface='{safeAlias}' nexthop={safeHop} metric={route.RouteMetric} store=persistent";
    }
}
