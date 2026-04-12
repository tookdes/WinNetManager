namespace WinNetManager.Models;

public class GhostAdapter
{
    public string DeviceInstanceId { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string Description { get; set; } = "";
    public string HardwareId { get; set; } = "";
    public bool IsPresent { get; set; }
    public bool IsSelected { get; set; }

    public string StatusDisplay => IsPresent ? "活跃" : "幽灵";
}
