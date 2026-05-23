using System.Net;

namespace WinNetManager.Models;

public class NetworkAdapterInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string IPv4Address { get; set; } = "";
    public string IPv6Address { get; set; } = "";
    public bool DhcpEnabled { get; set; }
    public string DhcpStatusDisplay => DhcpEnabled ? "DHCP" : "静态";
}
