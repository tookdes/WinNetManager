
namespace WinNetManager.Models;

public class GatewayMetricInfo
{
    public string InterfaceAlias { get; set; } = "";
    public string InterfaceIndex { get; set; } = "";
    public string AddressFamily { get; set; } = "";
    public string NextHop { get; set; } = "";
    public string RouteMetric { get; set; } = "";
}
