
using WinNetManager.Core.Models;
using Xunit;

namespace WinNetManager.Tests;

public class EngineStateMachineTests
{
    [Fact]
    public async Task ConsecutiveMatches_TriggerAfterThreshold()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 3, sustain: 0, dryRun: true);

        // 前两次失败：不触发
        for (int i = 0; i < 2; i++)
        {
            fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
            fx.Clock.AdvanceMinutes(1);
            await fx.TickAsync();
        }
        Assert.Null(fx.Engine.GetState(rule.Id)?.LastTriggerTime);

        // 第三次失败：触发
        fx.Clock.AdvanceMinutes(1);
        await fx.TickAsync();
        Assert.NotNull(fx.Engine.GetState(rule.Id)?.LastTriggerTime);
        Assert.Equal(1, fx.Engine.GetState(rule.Id)!.LifetimeTriggerCount);
    }

    [Fact]
    public async Task OneSuccessBetweenFailures_ResetsCounter()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 3, sustain: 0, dryRun: true);

        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();

        // 一次成功清零
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.True);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();

        // 再连续 3 次失败才触发（而不是累计到 3）
        for (int i = 0; i < 2; i++)
        {
            fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
            fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        }
        Assert.Null(fx.Engine.GetState(rule.Id)?.LastTriggerTime);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        Assert.NotNull(fx.Engine.GetState(rule.Id)?.LastTriggerTime);
    }

    [Fact]
    public async Task Unknown_DoNotCount_DoesNotResetOrTrigger()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        // 探针自身异常（如 ping 进程启动失败）→ Unknown，验证 DoNotCount 语义
        fx.Probes.Resolver = _ => TriState.Unknown;
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 3, sustain: 0, dryRun: true);

        for (int i = 0; i < 5; i++)
        {
            fx.Clock.AdvanceMinutes(1);
            await fx.TickAsync();
        }
        // Unknown（DoNotCount）：不累计也不触发
        Assert.Null(fx.Engine.GetState(rule.Id)?.LastTriggerTime);
        Assert.Equal(0, fx.Engine.GetState(rule.Id)!.ConsecutiveMatches);
    }

    [Fact]
    public async Task PingFail_MissingAddress_CountsAsFailure_WithDefaultPolicy()
    {
        // 光猫重拨后 IPv6 GUA 丢失：默认 TreatMissingAddressAsFailure=true，无地址按失败处理
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", null);
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 1, sustain: 0, dryRun: true);
        fx.RuleStore.Save(fx.RuleStore.Rules);
        fx.Clock.AdvanceMinutes(1);
        await fx.TickAsync();
        Assert.NotNull(fx.Engine.GetState(rule.Id)?.LastTriggerTime);
    }

    [Fact]
    public async Task CooldownAndRearm_PreventsImmediateRetrigger()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 1, sustain: 0, dryRun: true);
        rule.CooldownSeconds = 600;

        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        Assert.NotNull(fx.Engine.GetState(rule.Id)?.LastTriggerTime);
        Assert.False(fx.Engine.GetState(rule.Id)!.Armed); // 已解除武装

        // 持续失败：Armed=false，不再次触发
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        Assert.Equal(1, fx.Engine.GetState(rule.Id)!.LifetimeTriggerCount);

        // 恢复一次 → 重新武装；但仍在冷却期，不触发
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.True);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        Assert.True(fx.Engine.GetState(rule.Id)!.Armed);

        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        Assert.Equal(1, fx.Engine.GetState(rule.Id)!.LifetimeTriggerCount); // 冷却中未触发

        // 冷却结束后再失败 → 再次触发
        fx.Clock.AdvanceMinutes(11); // 跨过 600s 冷却
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        Assert.Equal(2, fx.Engine.GetState(rule.Id)!.LifetimeTriggerCount);
    }

    [Fact]
    public async Task BudgetPerDay_DisablesRuleAfterLimit()
    {
        // 预算在动作真正执行时记账（非 dry-run）；Notify 动作无害且立即完成
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 1, sustain: 0, dryRun: false);
        rule.Actions.Add(new NotifyAction { Title = "t", Message = "m" });
        rule.Budget.PerHour = 2;
        rule.Budget.PerDay = 99; // 只测小时滚动窗口；日预算设大避免干扰
        fx.RuleStore.Save(fx.RuleStore.Rules);

        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        for (int i = 0; i < 2; i++)
        {
            fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
            fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.True);
            fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
            fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        }
        await fx.Engine.WaitForIdleAsync();
        Assert.Equal(2, fx.Engine.GetState(rule.Id)!.LifetimeTriggerCount);

        // 触发第 3 次：预算拦截（滚动窗口：2 次都在最近 1 小时内）
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        await fx.Engine.WaitForIdleAsync();
        Assert.Equal(2, fx.Engine.GetState(rule.Id)!.LifetimeTriggerCount);
        Assert.True(fx.Engine.GetState(rule.Id)!.DisabledByBudget);

        // 滚动窗口：1 小时后再触发，旧计数滑出窗口，应能再次执行
        fx.Clock.AdvanceMinutes(70); // 超过 1 小时
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.True);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync(); // 恢复武装
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        await fx.Engine.WaitForIdleAsync();
        Assert.Equal(3, fx.Engine.GetState(rule.Id)!.LifetimeTriggerCount);
        Assert.False(fx.Engine.GetState(rule.Id)!.DisabledByBudget);
    }

    [Fact]
    public async Task Interval_Gating_SkipsEarlyEvaluation()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 1, sustain: 0, dryRun: true);
        rule.IntervalMinutes = 5;

        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        await fx.TickAsync(); // t0 评估→触发
        Assert.NotNull(fx.Engine.GetState(rule.Id)?.LastTriggerTime);

        // 未到间隔：1 分钟后的 tick 不评估
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.True);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        Assert.False(fx.Engine.GetState(rule.Id)!.Armed); // 状态未变（未评估到恢复）

        // 跨过间隔做一次健康评估 → 恢复武装
        fx.Clock.AdvanceMinutes(5); await fx.TickAsync();
        Assert.True(fx.Engine.GetState(rule.Id)!.Armed);

        // 再跨过间隔，故障 → 第二次触发
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Clock.AdvanceMinutes(5); await fx.TickAsync();
        Assert.Equal(2, fx.Engine.GetState(rule.Id)!.LifetimeTriggerCount);
    }

    [Fact]
    public async Task RuleHashChange_ResetsRuntimeState()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 3, sustain: 0, dryRun: true);

        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        Assert.Equal(1, fx.Engine.GetState(rule.Id)!.ConsecutiveMatches);

        // 修改规则（条件目标变化）→ 哈希变化 → 状态重置
        rule.Condition.Children[0] = new PingFailCondition { AdapterId = "A", Family = AddressFamilyKind.IPv6, Targets = { "2400:da00::6666" } };
        fx.Engine.ReloadRules();
        Assert.Equal(0, fx.Engine.GetState(rule.Id)!.ConsecutiveMatches);
    }

    [Fact]
    public async Task SustainSeconds_RequiresDuration()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 1, sustain: 120, dryRun: true);

        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync(); // t0+60，持续 0s
        Assert.Null(fx.Engine.GetState(rule.Id)?.LastTriggerTime);

        fx.Clock.AdvanceMinutes(1); await fx.TickAsync(); // t0+120，持续 60s < 120s
        Assert.Null(fx.Engine.GetState(rule.Id)?.LastTriggerTime);

        fx.Clock.AdvanceMinutes(1); await fx.TickAsync(); // t0+180，持续 120s
        Assert.NotNull(fx.Engine.GetState(rule.Id)?.LastTriggerTime);
    }

    [Fact]
    public async Task RecentWindow_SlidingMatch()
    {
        using var fx = new EngineFixture();
        fx.AddAdapter("A", "A", "10.0.0.2", "2001:db8::2");
        var rule = fx.AddPingRule("A", "2400:3200::1", consecutive: 99, sustain: 0, dryRun: true);
        rule.RecentWindowSize = 3;
        rule.RecentMinMatches = 2;

        // 失败、成功、失败 → 最近 3 次中 2 次失败 → 触发
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.True);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        fx.Probes.SetPing("A", AddressFamilyKind.IPv6, "2400:3200::1", TriState.False);
        fx.Clock.AdvanceMinutes(1); await fx.TickAsync();
        Assert.NotNull(fx.Engine.GetState(rule.Id)?.LastTriggerTime);
    }
}
