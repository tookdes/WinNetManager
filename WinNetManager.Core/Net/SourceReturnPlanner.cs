using System.Net;
using System.Net.Sockets;

namespace WinNetManager.Core.Net;

public sealed class ExistingDefaultRoute
{
    public string AddressFamily { get; set; } = "";
    public string DestinationPrefix { get; set; } = "";
    public string NextHop { get; set; } = "";
    public string InterfaceAlias { get; set; } = "";
    public string RouteMetric { get; set; } = "";
}

public sealed class SourceReturnRequest
{
    public string InterfaceAlias { get; set; } = "";
    public string? Ipv4NextHop { get; set; }
    public string? Ipv6NextHop { get; set; }
    public int Metric { get; set; } = 500;
    public bool SetStrongHost { get; set; } = true;
    public string? FirewallPorts { get; set; }
    public string FirewallDisplayName { get; set; } = "";
}

public sealed class SourceReturnAction
{
    public string Kind { get; set; } = "";
    public string AddressFamily { get; set; } = "";
    public string DestinationPrefix { get; set; } = "";
    public string NextHop { get; set; } = "";
    public string InterfaceAlias { get; set; } = "";
    public string Metric { get; set; } = "";
    public string? ExistingNextHop { get; set; }
    public string? ExistingMetric { get; set; }
    public string? Detail { get; set; }
}

public static class SourceReturnPlanner
{
    public const int DefaultMetric = 500;

    public static bool TryPlan(
        SourceReturnRequest request,
        IReadOnlyList<ExistingDefaultRoute> existing,
        out List<SourceReturnAction> actions,
        out string error)
    {
        actions = new List<SourceReturnAction>();
        error = "";
        if (request == null)
        {
            error = "请求为空。";
            return false;
        }

        string alias = request.InterfaceAlias?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(alias) || alias.Any(char.IsControl))
        {
            error = "接口别名不能为空，且不能包含控制字符。";
            return false;
        }

        if (request.Metric < 0 || request.Metric > 999999)
        {
            error = "跃点必须是 0 到 999999 之间的整数。";
            return false;
        }

        string? v4 = NormalizeHop(request.Ipv4NextHop, AddressFamily.InterNetwork, "IPv4", out error);
        if (error.Length > 0) return false;
        string? v6 = NormalizeHop(request.Ipv6NextHop, AddressFamily.InterNetworkV6, "IPv6", out error);
        if (error.Length > 0) return false;

        List<string>? firewallPorts = null;
        if (!string.IsNullOrWhiteSpace(request.FirewallPorts))
        {
            if (!FirewallPortSpec.TryParse(request.FirewallPorts, out firewallPorts, out error))
                return false;
        }

        if (v4 == null && v6 == null && !request.SetStrongHost && firewallPorts == null)
        {
            error = "请至少指定 IPv4 下一跳、IPv6 下一跳、强主机或防火墙端口。";
            return false;
        }

        var list = existing ?? Array.Empty<ExistingDefaultRoute>();
        if (v4 != null)
            PlanFamily("IPv4", "0.0.0.0/0", v4, alias, request.Metric, list, actions);
        if (v6 != null)
            PlanFamily("IPv6", "::/0", v6, alias, request.Metric, list, actions);

        if (request.SetStrongHost)
        {
            actions.Add(new SourceReturnAction
            {
                Kind = "SetStrongHost",
                InterfaceAlias = alias,
                Detail = "IPv4+IPv6",
            });
        }

        if (firewallPorts != null)
        {
            string displayName = string.IsNullOrWhiteSpace(request.FirewallDisplayName)
                ? $"WinNetManager_Inbound_{alias}_TCP"
                : request.FirewallDisplayName.Trim();
            actions.Add(new SourceReturnAction
            {
                Kind = "AddFirewall",
                InterfaceAlias = alias,
                Detail = string.Join(",", firewallPorts),
                NextHop = displayName,
            });
        }

        return true;
    }

    public static bool IsDefaultPrefix(string? prefix, string family)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return false;
        string p = prefix.Trim();
        if (family.Equals("IPv6", StringComparison.OrdinalIgnoreCase))
            return p == "::/0" || p == "::";
        return p == "0.0.0.0/0" || p == "0.0.0.0";
    }

    private static void PlanFamily(
        string family,
        string dest,
        string nextHop,
        string alias,
        int metric,
        IReadOnlyList<ExistingDefaultRoute> existing,
        List<SourceReturnAction> actions)
    {
        var onIface = existing.Where(r =>
                !string.IsNullOrWhiteSpace(r.InterfaceAlias)
                && r.InterfaceAlias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                && r.AddressFamily.Equals(family, StringComparison.OrdinalIgnoreCase)
                && IsDefaultPrefix(r.DestinationPrefix, family))
            .ToList();

        var sameHop = onIface.Where(r => (r.NextHop ?? "").Equals(nextHop, StringComparison.OrdinalIgnoreCase)).ToList();
        var others = onIface.Where(r => !(r.NextHop ?? "").Equals(nextHop, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var extra in others)
        {
            actions.Add(new SourceReturnAction
            {
                Kind = "DeleteRoute",
                AddressFamily = family,
                DestinationPrefix = dest,
                NextHop = extra.NextHop,
                InterfaceAlias = alias,
                Metric = extra.RouteMetric,
                ExistingNextHop = extra.NextHop,
                ExistingMetric = extra.RouteMetric,
            });
        }

        if (sameHop.Count == 0)
        {
            actions.Add(new SourceReturnAction
            {
                Kind = "AddRoute",
                AddressFamily = family,
                DestinationPrefix = dest,
                NextHop = nextHop,
                InterfaceAlias = alias,
                Metric = metric.ToString(),
            });
            return;
        }

        var keep = sameHop[0];
        for (int i = 1; i < sameHop.Count; i++)
        {
            actions.Add(new SourceReturnAction
            {
                Kind = "DeleteRoute",
                AddressFamily = family,
                DestinationPrefix = dest,
                NextHop = sameHop[i].NextHop,
                InterfaceAlias = alias,
                Metric = sameHop[i].RouteMetric,
                ExistingNextHop = sameHop[i].NextHop,
                ExistingMetric = sameHop[i].RouteMetric,
            });
        }

        if (!string.Equals(keep.RouteMetric?.Trim(), metric.ToString(), StringComparison.Ordinal))
        {
            actions.Add(new SourceReturnAction
            {
                Kind = "ReplaceRoute",
                AddressFamily = family,
                DestinationPrefix = dest,
                NextHop = nextHop,
                InterfaceAlias = alias,
                Metric = metric.ToString(),
                ExistingNextHop = keep.NextHop,
                ExistingMetric = keep.RouteMetric,
            });
        }
    }

    private static string? NormalizeHop(string? raw, AddressFamily expected, string label, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string hop = raw.Trim();
        if (!IPAddress.TryParse(hop, out var ip) || ip.AddressFamily != expected)
        {
            error = label == "IPv4" ? "IPv4 下一跳必须是有效的 IPv4 地址。" : "IPv6 下一跳必须是有效的 IPv6 地址。";
            return null;
        }
        return ip.ToString();
    }
}
