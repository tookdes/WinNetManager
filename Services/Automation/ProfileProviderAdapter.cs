
using WinNetManager.Core.Abstractions;

namespace WinNetManager.Services.Automation;

/// <summary>IProfileProvider 生产实现：包装 NetworkProfileService + 连接状态。</summary>
public sealed class ProfileProviderAdapter : IProfileProvider
{
    public IReadOnlyList<ProfileInfo> GetProfiles()
    {
        var list = new List<ProfileInfo>();
        var connected = NetworkListManagerService.GetConnectedNetworkIds();
        foreach (var p in NetworkProfileService.GetAllProfiles())
        {
            list.Add(new ProfileInfo
            {
                Guid = p.Guid.ToString(),
                Name = p.ProfileName,
                IsConnected = connected.Contains(p.Guid),
                DateCreated = p.DateCreated,
                DateLastConnected = p.DateLastConnected,
            });
        }
        return list;
    }
}
