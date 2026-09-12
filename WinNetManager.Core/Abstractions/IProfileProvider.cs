
namespace WinNetManager.Core.Abstractions;

/// <summary>网络配置文件只读视图（生产实现包装 NetworkProfileService）。</summary>
public sealed class ProfileInfo
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsConnected { get; set; }
    public DateTime? DateCreated { get; set; }
    public DateTime? DateLastConnected { get; set; }
}

public interface IProfileProvider
{
    IReadOnlyList<ProfileInfo> GetProfiles();
}
