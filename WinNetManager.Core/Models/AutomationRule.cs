
namespace WinNetManager.Core.Models;

/// <summary>单条规则的执行预算（防爆走）。</summary>
public sealed class RuleBudget
{
    /// <summary>每小时最多触发次数；0 表示不限制。</summary>
    public int PerHour { get; set; } = 3;
    /// <summary>每天最多触发次数；0 表示不限制。</summary>
    public int PerDay { get; set; } = 12;
}

/// <summary>一条自动化规则：条件树 + 时间门槛 + 有序动作链。</summary>
public sealed class AutomationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>影子模式：只评估与记录，不执行任何动作。</summary>
    public bool DryRun { get; set; } = true;
    /// <summary>评估间隔（分钟）。</summary>
    public int IntervalMinutes { get; set; } = 1;
    /// <summary>条件树（根节点）。</summary>
    public ConditionGroup Condition { get; set; } = new();
    /// <summary>Unknown 处理策略。</summary>
    public UnknownPolicy UnknownPolicy { get; set; } = UnknownPolicy.DoNotCount;
    /// <summary>连续满足次数（时间门槛之一）。</summary>
    public int ConsecutiveMatches { get; set; } = 3;
    /// <summary>持续满足秒数（时间门槛之二，从首次满足起算）。</summary>
    public int SustainSeconds { get; set; } = 60;
    /// <summary>滑窗模式：最近 N 次评估中至少 M 次满足。0 表示不使用滑窗。</summary>
    public int RecentWindowSize { get; set; }
    /// <summary>滑窗模式：至少满足次数。</summary>
    public int RecentMinMatches { get; set; }
    /// <summary>动作链执行前的延迟（秒），给网络自己恢复的机会。</summary>
    public int PreActionDelaySeconds { get; set; } = 30;
    /// <summary>触发后冷却（秒）：触发后必须经过冷却且恢复一次才允许再次触发。</summary>
    public int CooldownSeconds { get; set; } = 300;
    /// <summary>
    /// 重新武装超时（秒）：触发后若条件持续满足（例如重启太早、故障未恢复）导致一直无法恢复，
    /// 超过该时长后自动重新武装一次，允许再次触发（配合预算防止无限循环）。0 表示禁用。
    /// </summary>
    public int RearmTimeoutSeconds { get; set; } = 600;
    /// <summary>动作列表（按序执行）。</summary>
    public List<ActionBase> Actions { get; set; } = new();
    public RuleBudget Budget { get; set; } = new();
    /// <summary>用户备注。</summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// 规则"语义"内容哈希：条件/动作/时间门槛/预算。
    /// Enabled/DryRun/Name/Note/Id 变更不应重置运行时状态（否则禁用再启用会把冷却/预算/armed 清零）。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ContentHash
    {
        get
        {
            var payload = new
            {
                IntervalMinutes, ConsecutiveMatches, SustainSeconds,
                RecentWindowSize, RecentMinMatches, CooldownSeconds, PreActionDelaySeconds,
                RearmTimeoutSeconds, UnknownPolicy, Condition, Actions, Budget,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload, RuleJson.SerializerOptions);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            return Convert.ToHexString(sha.ComputeHash(bytes))[..16];
        }
    }
}

/// <summary>JSON 序列化选项（多态条件/动作）。</summary>
public static class RuleJson
{
    public static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
