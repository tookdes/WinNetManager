namespace WinNetManager.Models;

public class InterfaceMetricInfo
{
    public string InterfaceAlias { get; set; } = "";
    public string AddressFamily { get; set; } = "";
    public string InterfaceIndex { get; set; } = "";
    public bool AutomaticMetric { get; set; }
    public int InterfaceMetric { get; set; }
    public string AutoDisplay => AutomaticMetric ? "是" : "否";
}
