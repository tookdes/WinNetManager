using System.Text;
using WinNetManager.Models;
using WinNetManager.Services;
using Xunit;

namespace WinNetManager.Tests;

public class DynamicRouteTests
{
    [Fact]
    public void ValidateRoute_DynamicIpv4_AllowsEmptyNextHop()
    {
        var route = new RouteEntry
        {
            AddressFamily = "IPv4",
            DestinationPrefix = "10.0.0.0/7",
            InterfaceAlias = "以太网 2",
            RouteMetric = "25",
            NextHop = "",
            IsDynamicGateway = true
        };

        Assert.True(RoutingManager.ValidateRoute(route, out string error), error);
        Assert.Equal("", route.NextHop);
    }

    [Fact]
    public void ValidateRoute_DynamicIpv6_IsRejected()
    {
        var route = new RouteEntry
        {
            AddressFamily = "IPv6",
            DestinationPrefix = "2001:db8::/48",
            InterfaceAlias = "以太网 2",
            RouteMetric = "25",
            NextHop = "",
            IsDynamicGateway = true
        };

        Assert.False(RoutingManager.ValidateRoute(route, out string error));
        Assert.Contains("仅支持 IPv4", error, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicTaskName_IsDeterministicSafeAndChangesWithBusinessKey()
    {
        var route = DynamicRoute("以太网 2", "10.0.0.0/7");
        string first = route.DynamicTriggerTaskName;

        Assert.Equal(first, route.DynamicTriggerTaskName);
        Assert.StartsWith("WinNetManager_DynamicRoute_", first, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", first, StringComparison.Ordinal);
        Assert.DoesNotContain("/", first, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", first, StringComparison.Ordinal);

        route.DestinationPrefix = "10.0.0.0/8";
        Assert.NotEqual(first, route.DynamicTriggerTaskName);
    }

    [Fact]
    public void DynamicTaskXml_UsesSystemHighestAndNetworkEventTriggers()
    {
        var route = DynamicRoute("以太网 2", "10.0.0.0/7");
        var doc = RoutingManager.BuildDynamicTaskXml(route);
        string xml = doc.ToString();

        Assert.Contains("S-1-5-18", xml, StringComparison.Ordinal);
        Assert.Contains("ServiceAccount", xml, StringComparison.Ordinal);
        Assert.Contains("HighestAvailable", xml, StringComparison.Ordinal);
        Assert.Contains("Microsoft-Windows-Dhcp-Client/Operational", xml, StringComparison.Ordinal);
        Assert.Contains("EventID=50036 or EventID=50047", xml, StringComparison.Ordinal);
        Assert.Contains("Microsoft-Windows-NetworkProfile/Operational", xml, StringComparison.Ordinal);
        Assert.Contains("EventID=10000", xml, StringComparison.Ordinal);
        Assert.Contains("EncodedCommand", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.0/7", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicSyncScript_EscapesSingleQuoteInInterfaceAlias()
    {
        var route = DynamicRoute("O'Brien LAN", "10.0.0.0/7");
        string script = RoutingManager.BuildDynamicSyncScript(route, 3);

        Assert.Contains("$alias='O''Brien LAN'", script, StringComparison.Ordinal);
        Assert.Contains("$prefix='10.0.0.0/7'", script, StringComparison.Ordinal);
        Assert.Contains("PolicyStore PersistentStore", script, StringComparison.Ordinal);
        Assert.Contains("NO_GATEWAY", script, StringComparison.Ordinal);
        Assert.Contains("SYNCED|", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigRoundTrip_PreservesDynamicGatewayFlag()
    {
        var route = DynamicRoute("以太网 2", "10.0.0.0/7");
        route.NextHop = "13.8.16.1";

        var configs = ConfigExportService.ToRouteConfigs(new List<RouteEntry> { route });
        Assert.Single(configs);
        Assert.True(configs[0].IsDynamicGateway);

        var restored = ConfigExportService.ToRouteEntries(configs);
        Assert.Single(restored);
        Assert.True(restored[0].IsDynamicGateway);
        Assert.Equal("DynamicTask", restored[0].Store);
        Assert.Equal("13.8.16.1", restored[0].NextHop);

        Assert.Equal(2, new WinNetConfig().Version);
    }

    private static RouteEntry DynamicRoute(string alias, string prefix) =>
        new()
        {
            AddressFamily = "IPv4",
            DestinationPrefix = prefix,
            InterfaceAlias = alias,
            RouteMetric = "25",
            IsDynamicGateway = true
        };
}
