namespace WinNetManager.Models;

public class InterfaceMetricInfo
{
    public string InterfaceAlias { get; set; } = "";
    public string AddressFamily { get; set; } = "";
    public string InterfaceIndex { get; set; } = "";
    public bool AutomaticMetric { get; set; }
    public int InterfaceMetric { get; set; }
    public bool WeakHostSend { get; set; }
    public bool WeakHostReceive { get; set; }
    public bool IgnoreDefaultRoutes { get; set; }
    public string RouterDiscovery { get; set; } = "";
    public string AutoDisplay => AutomaticMetric ? "是" : "否";
    public string WeakHostSendDisplay => WeakHostSend ? "是" : "否";
    public string IgnoreDefaultRoutesDisplay => IgnoreDefaultRoutes ? "是" : "否";
    public string WeakHostReceiveDisplay => WeakHostReceive ? "是" : "否";
    public string HostModelDisplay => (WeakHostSend || WeakHostReceive) ? "弱主机" : "强主机";
}
