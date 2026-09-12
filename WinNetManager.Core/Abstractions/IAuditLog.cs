
namespace WinNetManager.Core.Abstractions;

public enum AuditEventKind
{
    EngineStart, EngineStop, Evaluate, Trigger, ActionStart, ActionEnd, ActionFailed,
    BudgetHit, CircuitBreaker, Suppressed, RuleDisabled, RuleEdited, Migration
}

/// <summary>追加式结构化审计日志（JSONL）。生产实现写文件，测试可用内存实现。</summary>
public interface IAuditLog
{
    void Write(AuditEntry entry);
}

public sealed class AuditEntry
{
    public DateTime Timestamp { get; set; }
    public string CorrelationId { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string RuleName { get; set; } = "";
    public AuditEventKind Kind { get; set; }
    public string Detail { get; set; } = "";
    public string ConditionSummary { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string ActionType { get; set; } = "";
    public string ActionParams { get; set; } = "";
    public string CommandPreview { get; set; } = "";
    public bool Ok { get; set; } = true;
    public int ExitCode { get; set; } = -1;
    public string Output { get; set; } = "";
}
