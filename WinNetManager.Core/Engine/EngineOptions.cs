
namespace WinNetManager.Core.Engine;

public sealed class EngineOptions
{
    /// <summary>应用启动宽限期（秒）：启动初期不评估，避免开机/拨号抖动误触发。</summary>
    public int StartupGraceSeconds { get; set; } = 60;
    /// <summary>网络变更/唤醒后的稳定等待期（秒）。</summary>
    public int NetworkChangeSettleSeconds { get; set; } = 90;
    /// <summary>全局熔断窗口（分钟）。</summary>
    public int GlobalCircuitWindowMinutes { get; set; } = 10;
    /// <summary>熔断窗口内允许的最大破坏性动作次数。</summary>
    public int GlobalCircuitMaxDestructive { get; set; } = 3;
    /// <summary>Restart/Renew 后网卡抑制窗口（秒）：期间相关条件按 Unknown 处理。</summary>
    public int SuppressionSeconds { get; set; } = 120;
    /// <summary>后台调度轮询间隔（秒）。</summary>
    public int TickSeconds { get; set; } = 15;
}
