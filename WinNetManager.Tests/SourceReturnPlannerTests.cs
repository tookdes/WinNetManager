using WinNetManager.Core.Net;
using Xunit;

namespace WinNetManager.Tests;

public class SourceReturnPlannerTests
{
    [Fact]
    public void PortSpec_ParsesListAndRanges()
    {
        Assert.True(FirewallPortSpec.TryParse("5555, 8443,9443,11443,44301-44399,21114-21119", out var ports, out var error));
        Assert.Equal("", error);
        Assert.Equal(new[] { "5555", "8443", "9443", "11443", "44301-44399", "21114-21119" }, ports);
        Assert.Equal("'5555','8443','9443','11443','44301-44399','21114-21119'", FirewallPortSpec.ToPowerShellArray(ports));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("80-70")]
    [InlineData("abc")]
    public void PortSpec_RejectsInvalid(string input)
    {
        Assert.False(FirewallPortSpec.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Planner_AddsHighMetricDefaultOnlyOnTargetInterface()
    {
        var existing = new List<ExistingDefaultRoute>
        {
            new()
            {
                AddressFamily = "IPv4",
                DestinationPrefix = "0.0.0.0/0",
                NextHop = "192.168.1.1",
                InterfaceAlias = "10G",
                RouteMetric = "1",
            },
            new()
            {
                AddressFamily = "IPv4",
                DestinationPrefix = "1.1.1.0/24",
                NextHop = "192.168.189.1",
                InterfaceAlias = "2.5G",
                RouteMetric = "1",
            },
        };

        var ok = SourceReturnPlanner.TryPlan(new SourceReturnRequest
        {
            InterfaceAlias = "2.5G",
            Ipv4NextHop = "192.168.189.1",
            Ipv6NextHop = "fe80::265a:5fff:feef:2d05",
            Metric = 500,
            SetStrongHost = true,
            FirewallPorts = "5555,8443,9443,11443,44301-44399,21114-21119",
        }, existing, out var actions, out var error);

        Assert.True(ok, error);
        Assert.DoesNotContain(actions, a => string.Equals(a.InterfaceAlias, "10G", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(actions, a => a.Kind == "DeleteRoute" && a.NextHop == "192.168.1.1");
        Assert.Contains(actions, a => a.Kind == "AddRoute" && a.AddressFamily == "IPv4" && a.DestinationPrefix == "0.0.0.0/0" && a.NextHop == "192.168.189.1" && a.Metric == "500");
        Assert.Contains(actions, a => a.Kind == "AddRoute" && a.AddressFamily == "IPv6" && a.DestinationPrefix == "::/0" && a.NextHop == "fe80::265a:5fff:feef:2d05");
        Assert.Contains(actions, a => a.Kind == "SetStrongHost" && a.InterfaceAlias == "2.5G");
        Assert.Contains(actions, a => a.Kind == "AddFirewall" && a.Detail!.Contains("44301-44399"));
    }

    [Fact]
    public void Planner_UpdatesMetricOnSameHopAndReplacesOtherHopOnSameNicOnly()
    {
        var existing = new List<ExistingDefaultRoute>
        {
            new() { AddressFamily = "IPv4", DestinationPrefix = "0.0.0.0/0", NextHop = "192.168.1.1", InterfaceAlias = "10G", RouteMetric = "1" },
            new() { AddressFamily = "IPv4", DestinationPrefix = "0.0.0.0/0", NextHop = "192.168.189.1", InterfaceAlias = "2.5G", RouteMetric = "1" },
            new() { AddressFamily = "IPv4", DestinationPrefix = "0.0.0.0/0", NextHop = "192.168.189.254", InterfaceAlias = "2.5G", RouteMetric = "20" },
        };

        var ok = SourceReturnPlanner.TryPlan(new SourceReturnRequest
        {
            InterfaceAlias = "2.5G",
            Ipv4NextHop = "192.168.189.1",
            Metric = 500,
            SetStrongHost = false,
        }, existing, out var actions, out var error);

        Assert.True(ok, error);
        Assert.Contains(actions, a => a.Kind == "DeleteRoute" && a.NextHop == "192.168.189.254" && a.InterfaceAlias == "2.5G");
        Assert.Contains(actions, a => a.Kind == "ReplaceRoute" && a.NextHop == "192.168.189.1" && a.Metric == "500");
        Assert.DoesNotContain(actions, a => a.InterfaceAlias == "10G");
        Assert.DoesNotContain(actions, a => a.Kind == "SetStrongHost");
    }

    [Fact]
    public void Planner_NoopsWhenExactRouteAlreadyExists()
    {
        var existing = new List<ExistingDefaultRoute>
        {
            new() { AddressFamily = "IPv4", DestinationPrefix = "0.0.0.0/0", NextHop = "192.168.189.1", InterfaceAlias = "2.5G", RouteMetric = "500" },
        };

        var ok = SourceReturnPlanner.TryPlan(new SourceReturnRequest
        {
            InterfaceAlias = "2.5G",
            Ipv4NextHop = "192.168.189.1",
            Metric = 500,
            SetStrongHost = false,
        }, existing, out var actions, out var error);

        Assert.True(ok, error);
        Assert.Empty(actions);
    }

    [Theory]
    [InlineData("::", "0", "IPv6", "::/0")]
    [InlineData("0.0.0.0", "0", "IPv4", "0.0.0.0/0")]
    [InlineData("::/0", "128", "IPv6", "::/0")]
    [InlineData("::", "", "IPv6", "::/128")]
    [InlineData("192.168.1.0", "24", "IPv4", "192.168.1.0/24")]
    public void CidrPrefix_HonorsUiPrefixLengthForDefaultRoutes(string input, string combo, string family, string expected)
    {
        Assert.Equal(expected, CidrPrefix.Resolve(input, combo, family));
    }
}

