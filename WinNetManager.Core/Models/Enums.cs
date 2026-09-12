
namespace WinNetManager.Core.Models;

public enum AddressFamilyKind
{
    IPv4,
    IPv6
}

public enum ProbeKind
{
    Ping,
    TcpConnect,
    AdapterUp,
    HasGlobalAddress
}

/// <summary>三态条件值：True/False/Unknown（探测异常、网卡不存在、无源地址等）。</summary>
public enum TriState
{
    Unknown = -1,
    False = 0,
    True = 1
}

public enum LogicOperator
{
    And,
    Or
}

/// <summary>Unknown 时如何处理：不累计不重置（默认）、按失败、按成功。</summary>
public enum UnknownPolicy
{
    DoNotCount,
    CountAsFailure,
    CountAsSuccess
}

public enum OnFailureMode
{
    /// <summary>动作失败后继续执行后续动作（Notify/HTTP 默认）。</summary>
    Continue,
    /// <summary>动作失败后中止整条规则的动作链（删除/重命名 Profile 默认）。</summary>
    Stop
}

public enum RunWhenMode
{
    /// <summary>总是执行。</summary>
    Always,
    /// <summary>仅当前一个动作失败时执行（表达"C 或 SOCKS5"回退链）。</summary>
    OnlyOnPreviousFailure,
    /// <summary>仅当前一个动作成功时执行。</summary>
    OnlyOnPreviousSuccess
}

/// <summary>Ping 多目标聚合模式：All=所有目标失败才算失败；Any=任一失败即失败。</summary>
public enum PingAggregateMode
{
    All,
    Any
}
