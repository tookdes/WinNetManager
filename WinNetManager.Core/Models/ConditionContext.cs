
using WinNetManager.Core.Abstractions;

namespace WinNetManager.Core.Models;

/// <summary>条件求值上下文：本次 tick 的观测快照 + 配置文件提供者 + 抑制查询。</summary>
public sealed class ConditionContext
{
    public ProbeSnapshot Snapshot { get; }
    public IProfileProvider Profiles { get; }
    /// <summary>网卡是否处于自动化动作后的抑制窗口（此时相关条件按 Unknown 处理）。</summary>
    public Func<string, bool> IsAdapterSuppressed { get; }

    public ConditionContext(ProbeSnapshot snapshot, IProfileProvider profiles, Func<string, bool> isAdapterSuppressed)
    {
        Snapshot = snapshot;
        Profiles = profiles;
        IsAdapterSuppressed = isAdapterSuppressed;
    }
}
