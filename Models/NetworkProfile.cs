namespace WinNetManager.Models;

public enum NetworkCategory
{
    Public = 0,
    Private = 1,
    DomainAuthenticated = 2
}

public class NetworkProfile
{
    public Guid Guid { get; set; }
    public string ProfileName { get; set; } = "";
    public string Description { get; set; } = "";
    public NetworkCategory Category { get; set; }
    public DateTime? DateCreated { get; set; }
    public DateTime? DateLastConnected { get; set; }
    public int NameType { get; set; }
    public bool Managed { get; set; }
    public bool IsConnected { get; set; }

    public string CategoryDisplay => Category switch
    {
        NetworkCategory.Public => "公用",
        NetworkCategory.Private => "专用",
        NetworkCategory.DomainAuthenticated => "域",
        _ => "未知"
    };

    public string StatusDisplay => IsConnected ? "当前连接" : "历史";
}
