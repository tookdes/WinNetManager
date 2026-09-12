
namespace WinNetManager.Core.Models;

/// <summary>规则的运行时状态（持久化，跨重启保留冷却/预算）。</summary>
public sealed class RuleRuntimeState
{
    public string RuleId { get; set; } = "";
    public string RuleHash { get; set; } = "";
    /// <summary>
    /// 记录状态创建时的执行模式。切换 dry-run ↔ 自动执行是安全边界变化，
    /// 不能沿用旧的 Armed/冷却状态；nullable 兼容旧版本状态文件。
    /// </summary>
    public bool? LastDryRun { get; set; }
    /// <summary>记录上次状态对应的启用状态，用于重新启用规则时重新武装。</summary>
    public bool? LastEnabled { get; set; }
    public int ConsecutiveMatches { get; set; }
    public bool Armed { get; set; } = true;
    public DateTime? FirstSatisfiedAt { get; set; }
    public DateTime? LastTriggerTime { get; set; }
    public DateTime? LastRecoveredTime { get; set; }
    public DateTime? LastEvaluatedAt { get; set; }
    public DateTime? HourWindowStart { get; set; }
    public DateTime? DayWindowStart { get; set; }
    public int HourTriggerCount { get; set; }
    public int DayTriggerCount { get; set; }
    public int LifetimeTriggerCount { get; set; }
    public bool DisabledByBudget { get; set; }
    /// <summary>最近 N 次评估结果（滑窗模式用）。</summary>
    public List<bool> RecentWindow { get; set; } = new();
    /// <summary>动作真正执行的时间戳（滚动预算窗口用，按小时/天滑动统计）。</summary>
    public List<DateTime> RecentTriggerTimes { get; set; } = new();
    /// <summary>最近一次评估结果摘要（UI 展示）。</summary>
    public string LastResult { get; set; } = "未评估";
    public DateTime? LastEvaluatedResultTime { get; set; }
    public string LastConditionSummary { get; set; } = "";
    public bool LastConditionTrue { get; set; }
    /// <summary>最近一次未触发的原因，供 UI/审计诊断“条件满足但为何没动作”。</summary>
    public string LastDecision { get; set; } = "";
}
