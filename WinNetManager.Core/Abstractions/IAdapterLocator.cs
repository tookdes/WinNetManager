
using WinNetManager.Core.Models;

namespace WinNetManager.Core.Abstractions;

/// <summary>网卡定位：按稳定 Id（NetworkInterface.Id）获取最新快照。生产实现每次重新枚举。</summary>
public interface IAdapterLocator
{
    IReadOnlyList<AdapterSnapshot> GetAdapters();
    AdapterSnapshot? GetAdapter(string adapterId);
}
