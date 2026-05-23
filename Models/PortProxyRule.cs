namespace WinNetManager.Models;

public class PortProxyRule
{
    public string Direction { get; set; } = "";
    public string ListenAddress { get; set; } = "";
    public string ListenPort { get; set; } = "";
    public string ConnectAddress { get; set; } = "";
    public string ConnectPort { get; set; } = "";
    public string Protocol { get; set; } = "tcp";

    /// <summary>
    /// 业务唯一键：Direction + ListenAddress + ListenPort
    /// </summary>
    public string Key => $"{Direction}/{ListenAddress}:{ListenPort}";

    public PortProxyRule Clone()
    {
        return new PortProxyRule
        {
            Direction = Direction,
            ListenAddress = ListenAddress,
            ListenPort = ListenPort,
            ConnectAddress = ConnectAddress,
            ConnectPort = ConnectPort,
            Protocol = Protocol
        };
    }

    public bool EqualsWithKeys(PortProxyRule? other)
    {
        if (other == null) return false;
        return Direction == other.Direction
            && ListenAddress == other.ListenAddress
            && ListenPort == other.ListenPort;
    }
}
