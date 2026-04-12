namespace WinNetManager.Models;

public class ConnectionInfo
{
    public Guid Guid { get; set; }
    public string Name { get; set; } = "";
    public string PnpInstanceId { get; set; } = "";
    public bool HasActiveAdapter { get; set; }
    public bool IsSelected { get; set; }

    public string StatusDisplay => HasActiveAdapter ? "活跃" : "无设备";
}
