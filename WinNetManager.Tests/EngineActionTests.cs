
using WinNetManager.Core.Abstractions;
using WinNetManager.Core.Models;
using Xunit;

namespace WinNetManager.Tests;

public class EngineActionTests
{
    [Fact]
    public async Task ManualEvaluateAndTrigger_ExecutesActionWhenCurrentConditionMatches()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 3, sustain: 120, dryRun: false);
        rule.Actions.Clear();
        rule.Actions.Add(new RestartAdapterAction { AdapterId = "A" });
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);

        string result = await fx.Engine.EvaluateAndTriggerNowAsync(rule.Id);
        await fx.Engine.WaitForIdleAsync();

        Assert.Contains("已触发", result);
        Assert.Single(fx.Shell.PowerShellScripts);
        Assert.Contains("Restart-NetAdapter", fx.Shell.PowerShellScripts[0]);
    }

    [Fact]
    public async Task ManualEvaluateAndTrigger_StillHonorsDryRun()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 1, sustain: 0, dryRun: true);
        rule.Actions.Clear();
        rule.Actions.Add(new RestartAdapterAction { AdapterId = "A" });
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);

        string result = await fx.Engine.EvaluateAndTriggerNowAsync(rule.Id);
        await fx.Engine.WaitForIdleAsync();

        Assert.Contains("dry-run", result);
        Assert.Empty(fx.Shell.PowerShellScripts);
    }

    private static async Task ForceTrigger(EngineFixture fx, AutomationRule rule, int times = 1)
    {
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        for (int i = 0; i < times; i++)
        {
            fx.Clock.AdvanceMinutes(1);
            await fx.TickAsync();
            // 恢复再失败，保证连续触发（Cooldown=0 默认）
            fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.True);
            fx.Clock.AdvanceMinutes(1);
            await fx.TickAsync();
            fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        }
        // 动作链现在后台执行（fire-and-forget），等它们完成再断言
        await fx.Engine.WaitForIdleAsync();
    }

    [Fact]
    public async Task DryRun_DoesNotExecuteActions()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 1, sustain: 0, dryRun: true);
        rule.Actions.Add(new RestartAdapterAction { AdapterId = "A" });
        fx.RuleStore.Save(fx.RuleStore.Rules);

        await ForceTrigger(fx, rule);
        Assert.Equal(1, fx.Engine.GetState(rule.Id)!.LifetimeTriggerCount);
        Assert.Empty(fx.Shell.PowerShellScripts);
    }

    [Fact]
    public async Task RestartAction_ExecutesPowerShell()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 1, sustain: 0, dryRun: false);
        rule.Actions.Add(new RestartAdapterAction { AdapterId = "A" });
        fx.RuleStore.Save(fx.RuleStore.Rules);

        await ForceTrigger(fx, rule);
        Assert.Single(fx.Shell.PowerShellScripts);
        Assert.Contains("Restart-NetAdapter", fx.Shell.PowerShellScripts[0]);
    }

    [Fact]
    public async Task SuppressedAdapter_PreventsCascadeTrigger()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        fx.AddAdapter("B", "B", "10.0.1.2", "2001:db9::2");
        var ruleA = fx.AddPingRule("A", "2400:3200::1", consecutive: 1, sustain: 0, dryRun: false);
        ruleA.Actions.Add(new RestartAdapterAction { AdapterId = "A" });
        fx.RuleStore.Save(fx.RuleStore.Rules);

        var ruleBoth = new AutomationRule
        {
            Name = "AB全挂", Enabled = true, DryRun = true, IntervalMinutes = 1,
            ConsecutiveMatches = 1, SustainSeconds = 0, PreActionDelaySeconds = 0, CooldownSeconds = 0,
            Condition = new ConditionGroup
            {
                Operator = LogicOperator.And,
                Children =
                {
                    new PingFailCondition { AdapterId = "A", Family = AddressFamilyKind.IPv6, Targets = { "2400:3200::1" } },
                    new PingFailCondition { AdapterId = "B", Family = AddressFamilyKind.IPv6, Targets = { "2400:3200::1" } },
                }
            },
        };
        fx.RuleStore.Rules.Add(ruleBoth);

        // 触发规则 A（重启 A → A 进入抑制窗口）
        await ForceTrigger(fx, ruleA);

        // B 失败，A 被抑制（Unknown）→ AB全挂 不触发
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Probes.SetPing("B", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Clock.AdvanceMinutes(1);
        await fx.TickAsync();
        Assert.Null(fx.Engine.GetState(ruleBoth.Id)?.LastTriggerTime);
    }

    [Fact]
    public async Task FallbackAction_OnlyOnPreviousFailure()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 1, sustain: 0, dryRun: false);
        rule.Actions.Add(new HttpRequestAction { Url = "https://example.com/hook", ViaAdapterId = "A", TimeoutMs = 5000 });
        rule.Actions.Add(new HttpRequestAction { Url = "https://example.com/hook2", SocksProxy = "127.0.0.1:1080", RunWhen = RunWhenMode.OnlyOnPreviousFailure });
        fx.RuleStore.Save(fx.RuleStore.Rules);

        await ForceTrigger(fx, rule);
        // FakeShell 记录 2 次 curl 调用（第一个失败 → fallback 执行）
        Assert.Equal(2, fx.Shell.ExeCalls.Count);
    }

    [Fact]
    public async Task MergeProfiles_PartialFailure_StopsChain()
    {
        using var fx = new EngineFixture();
        fx.Profiles.Profiles.Add(new ProfileInfo { Name = "网络", IsConnected = false, DateLastConnected = new DateTime(2026, 1, 1) });
        fx.Profiles.Profiles.Add(new ProfileInfo { Name = "网络 2", IsConnected = false, DateLastConnected = new DateTime(2026, 1, 2) });

        var rule = new AutomationRule
        {
            Name = "整理", Enabled = true, DryRun = false, IntervalMinutes = 1,
            ConsecutiveMatches = 1, SustainSeconds = 0, PreActionDelaySeconds = 0, CooldownSeconds = 0,
            Condition = new ConditionGroup
            {
                Operator = LogicOperator.And,
                Children = { new ProfileExistsCondition { Name = "网络 2" } }
            },
        };
        fx.ProfileMutator.DeleteResult = true;
        fx.ProfileMutator.RenameResult = false;
        fx.ProfileMutator.RenameError = "模拟重命名失败";
        rule.Actions.Add(new MergeNetworkProfilesAction { KeepName = "网络", DuplicateName = "网络 2" });
        rule.Actions.Add(new NotifyAction { Title = "不应执行", Message = "Stop 后不会到这里" });
        fx.RuleStore.Rules.Add(rule);
        fx.RuleStore.Save(fx.RuleStore.Rules);

        fx.Clock.AdvanceMinutes(1);
        await fx.TickAsync();
        await fx.Engine.WaitForIdleAsync();

        Assert.Single(fx.ProfileMutator.Deleted);
        Assert.Empty(fx.ProfileMutator.Renamed);
        Assert.True(fx.ProfileMutator.BackupCalls >= 1);
        Assert.NotNull(fx.Engine.GetState(rule.Id)!.LastTriggerTime);
    }

    [Fact]
    public async Task MergeProfiles_AllowsConnectedDuplicate_ButRejectsConnectedKeep()
    {
        // 语义修正：重复项「网络 2」为当前连接是典型场景（允许重命名它）；
        // 但 keep「网络」若为当前连接则禁止删除。
        using var fx = new EngineFixture();
        fx.Profiles.Profiles.Add(new ProfileInfo { Name = "网络", IsConnected = false });
        fx.Profiles.Profiles.Add(new ProfileInfo { Name = "网络 2", IsConnected = true, DateLastConnected = new DateTime(2026, 1, 3), DateCreated = new DateTime(2026, 1, 3) });

        var rule = new AutomationRule
        {
            Name = "整理", Enabled = true, DryRun = false, IntervalMinutes = 1,
            ConsecutiveMatches = 1, SustainSeconds = 0, PreActionDelaySeconds = 0, CooldownSeconds = 0,
            Condition = new ConditionGroup { Operator = LogicOperator.And, Children = { new ProfileExistsCondition { Name = "网络 2" } } },
            Actions = { new MergeNetworkProfilesAction { KeepName = "网络", DuplicateName = "网络 2" } },
        };
        fx.RuleStore.Rules.Add(rule);
        fx.RuleStore.Save(fx.RuleStore.Rules);

        fx.Clock.AdvanceMinutes(1);
        await fx.TickAsync();
        await fx.Engine.WaitForIdleAsync();

        // 重复项是当前连接 → 允许执行：删除 keep「网络」，重命名 dup「网络 2」→「网络」
        Assert.Single(fx.ProfileMutator.Deleted);
        Assert.Single(fx.ProfileMutator.Renamed);
        Assert.Equal(("网络 2", "网络"), fx.ProfileMutator.Renamed[0]);

        // keep 为当前连接 → 拒绝
        using var fx2 = new EngineFixture();
        fx2.Profiles.Profiles.Add(new ProfileInfo { Name = "网络", IsConnected = true });
        fx2.Profiles.Profiles.Add(new ProfileInfo { Name = "网络 2", IsConnected = false, DateLastConnected = new DateTime(2026, 1, 3), DateCreated = new DateTime(2026, 1, 3) });
        var rule2 = new AutomationRule
        {
            Name = "整理2", Enabled = true, DryRun = false, IntervalMinutes = 1,
            ConsecutiveMatches = 1, SustainSeconds = 0, PreActionDelaySeconds = 0, CooldownSeconds = 0,
            Condition = new ConditionGroup { Operator = LogicOperator.And, Children = { new ProfileExistsCondition { Name = "网络 2" } } },
            Actions = { new MergeNetworkProfilesAction { KeepName = "网络", DuplicateName = "网络 2" } },
        };
        fx2.RuleStore.Rules.Add(rule2);
        fx2.RuleStore.Save(fx2.RuleStore.Rules);
        fx2.Clock.AdvanceMinutes(1);
        await fx2.TickAsync();
        await fx2.Engine.WaitForIdleAsync();
        Assert.Empty(fx2.ProfileMutator.Deleted);
        Assert.Empty(fx2.ProfileMutator.Renamed);
    }

    [Fact]
    public async Task CircuitBreaker_BlocksDestructiveActions()
    {
        // 用「删除配置文件」这类无抑制副作用的破坏性动作来测熔断，避免与 Restart 的抑制窗口耦合
        using var fx = new EngineFixture(suppressionSeconds: 0);
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        fx.Profiles.Profiles.Add(new ProfileInfo { Name = "网络", IsConnected = false });
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 1, sustain: 0, dryRun: false);
        rule.Actions.Add(new DeleteNetworkProfileAction { Name = "网络" });
        fx.RuleStore.Save(fx.RuleStore.Rules);

        await ForceTrigger(fx, rule, times: 3);
        Assert.Equal(3, fx.ProfileMutator.Deleted.Count);

        await ForceTrigger(fx, rule, times: 1);
        Assert.Equal(3, fx.ProfileMutator.Deleted.Count); // 熔断拦截
    }

    [Fact]
    public void ContentHash_IgnoresEnabledNameNote()
    {
        var rule = RuleTemplates.Ipv6SelfHealSoft("adapter-a", "2400:3200::1");
        string h1 = rule.ContentHash;
        rule.Enabled = true;
        rule.DryRun = false;
        rule.Name = "改名";
        rule.Note = "备注";
        Assert.Equal(h1, rule.ContentHash);

        // 语义内容变化 → 哈希变化
        rule.Condition.Children[0] = new PingFailCondition { AdapterId = "adapter-b", Family = AddressFamilyKind.IPv6, Targets = { "2400:da00::6666" } };
        Assert.NotEqual(h1, rule.ContentHash);
    }

    [Fact]
    public void PingSucceeds_NoSourceAddress_ReturnsUnknown()
    {
        // 与 PingFail 一致：无源地址时按 Unknown 处理（不误判为失败）
        var condition = new PingSucceedsCondition { AdapterId = "A", Family = AddressFamilyKind.IPv6, Targets = { "2400:3200::1" } };
        var snapshot = new ProbeSnapshot();
        snapshot.Adapters["A"] = new AdapterSnapshot { Id = "A", Name = "A", Up = true };
        var ctx = new ConditionContext(snapshot, new FakeProfileProvider(), _ => false);
        Assert.Equal(TriState.Unknown, condition.Evaluate(ctx));
    }

    [Fact]
    public async Task SlidingWindow_UnknownWithDoNotCount_DoesNotBurnSlot()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 99, sustain: 0, dryRun: true);
        rule.RecentWindowSize = 2;
        rule.RecentMinMatches = 2;

        // 失败、Unknown(DoNotCount，不占槽)、失败 → 窗口恰好 [失败,失败] → 触发
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.Unknown);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();

        Assert.NotNull(fx.Engine.GetState(rule.Id)?.LastTriggerTime);
    }

    [Fact]
    public async Task RearmTimeout_AllowsRetryAfterUnresolvedFault()
    {
        // 触发后故障未恢复（条件持续 True），超过重新武装超时后应能再次触发
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 1, sustain: 0, dryRun: true);
        rule.RearmTimeoutSeconds = 300;
        fx.RuleStore.Save(fx.RuleStore.Rules);

        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        Assert.Equal(1, fx.Engine.GetState(rule.Id)!.LifetimeTriggerCount);
        Assert.False(fx.Engine.GetState(rule.Id)!.Armed);

        // 未到超时：条件持续满足，不再次触发
        for (int i = 0; i < 3; i++)
        {
            fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        }
        Assert.Equal(1, fx.Engine.GetState(rule.Id)!.LifetimeTriggerCount);

        // 超过 RearmTimeoutSeconds（300s）后自动重新武装 → 再次触发
        fx.Clock.AdvanceMinutes(6); // 跨过超时
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        Assert.Equal(2, fx.Engine.GetState(rule.Id)!.LifetimeTriggerCount);
    }

    [Fact]
    public async Task DryRun_DoesNotConsumeBudget()
    {
        // 预算在动作真正执行时记账：dry-run 触发不应扣预算
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 1, sustain: 0, dryRun: true);
        fx.RuleStore.Save(fx.RuleStore.Rules);
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Clock.AdvanceMinutes(1);
        await fx.TickAsync();
        Assert.Empty(fx.Engine.GetState(rule.Id)!.RecentTriggerTimes);
    }

}
