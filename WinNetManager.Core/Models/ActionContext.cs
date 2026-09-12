
using WinNetManager.Core.Abstractions;

namespace WinNetManager.Core.Models;

/// <summary>动作执行上下文。</summary>
public sealed class ActionContext
{
    public required IShell Shell { get; init; }
    public required IProfileProvider Profiles { get; init; }
    public required IProfileMutator ProfileMutator { get; init; }
    public required IAdapterLocator Adapters { get; init; }
    public required IAuditLog Audit { get; init; }
    public string CorrelationId { get; init; } = "";
    public string RuleId { get; init; } = "";
    public string RuleName { get; init; } = "";
}

public sealed class ActionResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public bool IsDestructive { get; set; }
    public string? CommandPreview { get; set; }
    /// <summary>动作执行后需要进入抑制窗口的网卡 Id（Restart/Renew）。</summary>
    public IReadOnlyList<string> SuppressAdapterIds { get; set; } = Array.Empty<string>();
    /// <summary>抑制时长（秒）；&lt;=0 时用引擎全局配置。Renew 短、Restart 长。</summary>
    public int SuppressDurationSeconds { get; set; } = -1;
}
