
namespace WinNetManager.Core.Models;

/// <summary>
/// 内置场景模板：一键生成规则（默认 dry-run + 禁用，用户确认后开启）。
/// </summary>
public static class RuleTemplates
{
    /// <summary>模板 1a：IPv6 自愈（软刷新）——条件持续满足后先 Renew6。</summary>
    public static AutomationRule Ipv6SelfHealSoft(string adapterId, string target)
    {
        return new AutomationRule
        {
            Name = "IPv6 自愈（软刷新）",
            Enabled = false,
            DryRun = true,
            IntervalMinutes = 1,
            ConsecutiveMatches = 3,
            SustainSeconds = 60,
            PreActionDelaySeconds = 30,
            CooldownSeconds = 300,
            Condition = new ConditionGroup
            {
                Operator = LogicOperator.And,
                Children =
                {
                    new AdapterUpCondition { AdapterId = adapterId },
                    // 不 AND HasAddress：PingFailCondition 已把"无 IPv6 地址"视为失败（光猫重拨后常丢失 GUA）
                    new PingFailCondition { AdapterId = adapterId, Family = AddressFamilyKind.IPv6, Targets = { target }, TimeoutMs = 3000 },
                }
            },
            Actions =
            {
                new NotifyAction { Title = "IPv6 掉线", Message = $"检测到 {adapterId} IPv6 无法到达 {target}，准备软刷新。", Popup = false },
                new RenewAdapterAction { AdapterId = adapterId, Family = AddressFamilyKind.IPv6 },
            },
            Budget = new RuleBudget { PerHour = 3, PerDay = 12 },
            Note = "模板：IPv6 软刷新（ipconfig /renew6），不物理断网。仍失败请配合「IPv6 自愈（重启）」规则。",
        };
    }

    /// <summary>模板 1b：IPv6 自愈（升级梯保底）——Renew 无效后重启网卡。</summary>
    public static AutomationRule Ipv6SelfHealRestart(string adapterId, string target)
    {
        return new AutomationRule
        {
            Name = "IPv6 自愈（重启保底）",
            Enabled = false,
            DryRun = true,
            IntervalMinutes = 1,
            ConsecutiveMatches = 6,
            SustainSeconds = 180,
            PreActionDelaySeconds = 30,
            CooldownSeconds = 600,
            Condition = new ConditionGroup
            {
                Operator = LogicOperator.And,
                Children =
                {
                    new AdapterUpCondition { AdapterId = adapterId },
                    // 不 AND HasAddress：PingFailCondition 已把"无 IPv6 地址"视为失败
                    new PingFailCondition { AdapterId = adapterId, Family = AddressFamilyKind.IPv6, Targets = { target }, TimeoutMs = 3000 },
                }
            },
            Actions =
            {
                new NotifyAction { Title = "IPv6 掉线（重启保底）", Message = $"软刷新无效，重启 {adapterId} 网卡。", Popup = true },
                new RestartAdapterAction { AdapterId = adapterId },
                new WaitUntilAdapterHasAddressAction { AdapterId = adapterId, Family = AddressFamilyKind.IPv6, TimeoutSeconds = 120 },
            },
            Budget = new RuleBudget { PerHour = 2, PerDay = 6 },
            Note = "模板：软刷新（/renew6）仍失败后物理重启网卡。重启后进入抑制窗口，相关条件按未知处理。",
        };
    }

    /// <summary>模板 2：IPv4 掉线 → 重启 A，并经 B 的 IPv4 接口访问 URL 通知。</summary>
    public static AutomationRule Ipv4RestartAndNotifyVia(string adapterA, string adapterB, string ipv4Target, string url)
    {
        return new AutomationRule
        {
            Name = "IPv4 掉线自启+通知",
            Enabled = false,
            DryRun = true,
            IntervalMinutes = 1,
            ConsecutiveMatches = 3,
            SustainSeconds = 60,
            PreActionDelaySeconds = 30,
            CooldownSeconds = 600,
            Condition = new ConditionGroup
            {
                Operator = LogicOperator.And,
                Children =
                {
                    new AdapterUpCondition { AdapterId = adapterA },
                    // 不 AND HasAddress：PingFailCondition 已把"无 IPv4 地址/APIPA"视为失败
                    new PingFailCondition { AdapterId = adapterA, Family = AddressFamilyKind.IPv4, Targets = { ipv4Target }, TimeoutMs = 3000 },
                }
            },
            Actions =
            {
                new NotifyAction { Title = "IPv4 掉线", Message = $"{adapterA} IPv4 无法到达 {ipv4Target}，重启网卡。", Popup = true },
                new RestartAdapterAction { AdapterId = adapterA },
                new WaitAction { Seconds = 30 },
                new HttpRequestAction { Url = url, ViaAdapterId = adapterB, ViaAdapterFamily = AddressFamilyKind.IPv4, TimeoutMs = 10000, ExpectedStatus = null },
            },
            Budget = new RuleBudget { PerHour = 2, PerDay = 6 },
            Note = "模板：A 的 IPv4 掉线后重启 A，稍等 30 秒经 B 的 IPv4 接口访问 URL（建议用固定 IP 或 --resolve，避免 DNS 走默认路由）。",
        };
    }

    /// <summary>模板 3：A、B 全挂 → 经 C 的 IPv4 或 SOCKS5 代理访问 URL 通知（4G 兜底，带流量预算）。</summary>
    public static AutomationRule FallbackWhenAllDown(string adapterA, string adapterB, string adapterC, string targetA, string targetB, string url, string? socksProxy)
    {
        var rule = new AutomationRule
        {
            Name = "A/B 全挂走 C 兜底",
            Enabled = false,
            DryRun = true,
            IntervalMinutes = 1,
            ConsecutiveMatches = 3,
            SustainSeconds = 60,
            PreActionDelaySeconds = 60,
            CooldownSeconds = 1800,
            Condition = new ConditionGroup
            {
                Operator = LogicOperator.And,
                Children =
                {
                    new PingFailCondition { AdapterId = adapterA, Family = AddressFamilyKind.IPv4, Targets = { targetA }, TimeoutMs = 3000 },
                    new PingFailCondition { AdapterId = adapterB, Family = AddressFamilyKind.IPv4, Targets = { targetB }, TimeoutMs = 3000 },
                }
            },
            Actions = { new NotifyAction { Title = "双线全挂", Message = "A、B 均无法连通，准备走 4G 兜底通知。", Popup = true } },
            Budget = new RuleBudget { PerHour = 1, PerDay = 4 },
            Note = "模板：A、B 同时不通（同一时刻快照）才触发；4G 路径昂贵，请保持默认预算。",
        };
        if (!string.IsNullOrWhiteSpace(url))
        {
            rule.Actions.Add(new HttpRequestAction
            {
                Url = url,
                ViaAdapterId = adapterC,
                ViaAdapterFamily = AddressFamilyKind.IPv4,
                TimeoutMs = 10000,
            });
        }
        if (!string.IsNullOrWhiteSpace(socksProxy))
        {
            rule.Actions.Add(new HttpRequestAction
            {
                Url = url,
                SocksProxy = socksProxy,
                TimeoutMs = 10000,
                RunWhen = RunWhenMode.OnlyOnPreviousFailure,
            });
        }
        return rule;
    }

    /// <summary>模板 4：网络配置文件整理（受保护复合动作，默认永远 dry-run，需显式开启）。</summary>
    public static AutomationRule MergeNetworkProfiles(string keepName, string duplicateName)
    {
        return new AutomationRule
        {
            Name = "网络配置文件整理",
            Enabled = false,
            DryRun = true,
            IntervalMinutes = 5,
            ConsecutiveMatches = 2,
            SustainSeconds = 0,
            PreActionDelaySeconds = 10,
            CooldownSeconds = 3600,
            Condition = new ConditionGroup
            {
                Operator = LogicOperator.And,
                Children =
                {
                    new ProfileExistsCondition { Name = duplicateName },
                    new ProfileLastConnectedAfterCondition { NewerName = duplicateName, OlderName = keepName },
                }
            },
            Actions =
            {
                new MergeNetworkProfilesAction { KeepName = keepName, DuplicateName = duplicateName },
            },
            Budget = new RuleBudget { PerHour = 1, PerDay = 1 },
            Note = "模板（实验性）：检测到重复编号的「网络 2」且其最后连接晚于「网络」时，备份后删除旧「网络」并把「网络 2」改名为「网络」。默认永远 dry-run，请先观察一周再手动开启。",
        };
    }
}
