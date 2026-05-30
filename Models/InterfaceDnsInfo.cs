namespace WinNetManager.Models;

public class InterfaceDnsInfo
{
    public string InterfaceAlias { get; set; } = "";
    public string InterfaceIndex { get; set; } = "";
    public string AddressFamily { get; set; } = "";
    public string ServerAddresses { get; set; } = ""; // Comma separated
}
