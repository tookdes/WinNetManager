namespace WinNetManager.Models;

public class AdapterToggleInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IPv6Enabled { get; set; }
    public string IPv6PrivacyState { get; set; } = "N/A";
    /// <summary>0=Default(DHCP), 1=Enabled, 2=Disabled</summary>
    public int NetBiosOption { get; set; }

    // ── Display helpers ──

    public string IPv6Display => IPv6Enabled ? "已启用" : "已禁用";
    public string IPv6PrivacyDisplay => IPv6PrivacyState switch
    {
        "Enabled" or "1" => "已启用",
        "Disabled" or "0" => "已禁用",
        _ => IPv6PrivacyState
    };
    public string NetBiosDisplay => NetBiosOption switch
    {
        0 => "默认 (DHCP)",
        1 => "已启用",
        2 => "已禁用",
        _ => NetBiosOption.ToString()
    };
}
