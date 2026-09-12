
namespace WinNetManager.Core.Engine;

public enum EngineEventKind { Log, RuleStateChanged, EngineStateChanged, Notify }

public sealed class EngineEvent
{
    public EngineEventKind Kind { get; init; }
    public string Message { get; init; } = "";
    public string? RuleId { get; init; }
    public string? RuleName { get; init; }
    /// <summary>0=info 1=warn 2=danger</summary>
    public int Severity { get; init; }
    /// <summary>通知动作是否请求弹窗（Notify 事件使用）。</summary>
    public bool Popup { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
}
