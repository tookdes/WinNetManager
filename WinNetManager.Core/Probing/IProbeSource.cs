
using WinNetManager.Core.Models;

namespace WinNetManager.Core.Probing;

/// <summary>探针协调器：一次调用返回整份观测快照（网卡 + 去重后的探针结果）。</summary>
public interface IProbeSource
{
    Task<ProbeSnapshot> ProbeAsync(IReadOnlyCollection<ProbeRequest> requests, CancellationToken ct);
}
