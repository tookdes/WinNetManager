
using System.Collections.Concurrent;
using WinNetManager.Core.Abstractions;
using WinNetManager.Core.Models;
using WinNetManager.Core.Probing;
using WinNetManager.Core.Storage;

namespace WinNetManager.Core.Engine;

/// <summary>
/// 自动化规则引擎：
/// 1) 一次 tick 收集所有启用规则的探针请求（去重）→ 共享观测快照；
/// 2) 逐规则求值（三态条件树 + 时间门槛 + 冷却 + 预算）；
/// 3) 触发时按序执行动作链（全局串行 + 熔断 + 抑制窗口 + 审计）。
/// 引擎本身无 UI 依赖，全部副作用通过注入接口完成。
/// </summary>
public sealed class AutomationEngine : IDisposable
{
    private readonly IClock _clock;
    private readonly IProbeSource _probes;
    private readonly IProfileProvider _profiles;
    private readonly IProfileMutator _profileMutator;
    private readonly IAdapterLocator _adapters;
    private readonly IShell _shell;
    private readonly IRuleStore _ruleStore;
    private readonly IRuntimeStateStore _stateStore;
    private readonly IAuditLog _audit;
    private readonly EngineOptions _options;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _actionLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _suppressedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DateTime> _destructiveTimes = new();
    private readonly List<Task> _actionTasks = new();

    private List<AutomationRule> _rules = new();
    private ConcurrentDictionary<string, RuleRuntimeState> _states = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _running;
    private DateTime? _startedAt;
    private DateTime? _settleUntil;
    private bool _disposed;

    public bool GlobalEnabled { get; set; }
    public bool IsRunning => _running;

    public event Action<EngineEvent>? EventRaised;

    public AutomationEngine(
        IClock clock,
        IProbeSource probes,
        IProfileProvider profiles,
        IProfileMutator profileMutator,
        IAdapterLocator adapters,
        IShell shell,
        IRuleStore ruleStore,
        IRuntimeStateStore stateStore,
        IAuditLog audit,
        EngineOptions? options = null)
    {
        _clock = clock;
        _probes = probes;
        _profiles = profiles;
        _profileMutator = profileMutator;
        _adapters = adapters;
        _shell = shell;
        _ruleStore = ruleStore;
        _stateStore = stateStore;
        _audit = audit;
        _options = options ?? new EngineOptions();

        _rules = _ruleStore.Load();
        _states = new ConcurrentDictionary<string, RuleRuntimeState>(_stateStore.Load(), StringComparer.Ordinal);
        // 清理已删除规则的陈旧状态
        var ids = _rules.Select(r => r.Id).ToHashSet();
        foreach (var k in _states.Keys.Where(k => !ids.Contains(k)).ToList())
            _states.TryRemove(k, out _);
        // 规则哈希变化 → 重置旧状态（条件变了不能继承旧计数）
        foreach (var r in _rules)
        {
            if (_states.TryGetValue(r.Id, out var st) && NeedsRuntimeReset(r, st))
                _states[r.Id] = NewState(r, _clock.Now);
        }
    }

    // ---------------- 生命周期 ----------------

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            // 旧循环仍在收尾时不允许再开新循环，避免两个循环并存
            if (_loopTask is { IsCompleted: false })
            {
                try { _loopTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            }
            if (_loopTask is { IsCompleted: false }) return;
            _running = true;
            _startedAt = _clock.Now;
            _graceNotified = false;
            _settleNotified = false;
            // 捕获局部 CTS；不能在 lambda 中再次读取可被 Stop() 清空的 _cts，
            // 否则快速启动/停止时可能出现 NullReferenceException，循环根本没有跑起来。
            var cts = new CancellationTokenSource();
            _cts = cts;
            _loopTask = Task.Run(() => LoopAsync(cts.Token));
        }
        _audit.Write(new AuditEntry
        {
            Timestamp = _clock.Now,
            Kind = AuditEventKind.EngineStart,
            Detail = "自动化引擎已启动。",
        });
        Emit(EngineEventKind.Log, "自动化引擎已启动。", severity: 0);
        Emit(EngineEventKind.EngineStateChanged, "running");
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_lock)
        {
            if (!_running) { cts = null; task = null; }
            else { _running = false; cts = _cts; _cts = null; task = _loopTask; }
        }
        if (cts == null) return;
        try { cts.Cancel(); } catch { }
        try { task?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        try { cts.Dispose(); } catch { }
        lock (_lock)
        {
            if (ReferenceEquals(_loopTask, task)) _loopTask = null;
        }
        // 等待已入队的动作链收尾（有限超时；Restart/Renew 等不响应取消的进程可能跑完）
        Task[] pending;
        lock (_lock) pending = _actionTasks.ToArray();
        if (pending.Length > 0)
        {
            try { Task.WaitAll(pending, TimeSpan.FromSeconds(5)); } catch { }
        }
        SaveStates();
        _audit.Write(new AuditEntry
        {
            Timestamp = _clock.Now,
            Kind = AuditEventKind.EngineStop,
            Detail = "自动化引擎已停止。",
        });
        Emit(EngineEventKind.Log, "自动化引擎已停止。", severity: 0);
        Emit(EngineEventKind.EngineStateChanged, "stopped");
    }

    /// <summary>系统网络变更/唤醒时调用：进入稳定等待期，避免抖动误触发。</summary>
    public void NotifyNetworkChanged()
    {
        // 去抖：已在稳定等待期内时不再延长，避免频繁网络事件（DHCP/IPv6 RA/隐私地址轮换）
        // 让引擎永远处于暂停状态
        if (_settleUntil != null && _clock.Now < _settleUntil.Value) return;
        _settleUntil = _clock.Now.AddSeconds(_options.NetworkChangeSettleSeconds);
        _settleNotified = false;
        Emit(EngineEventKind.Log, $"检测到网络变更/唤醒，{_options.NetworkChangeSettleSeconds} 秒内暂停评估。", severity: 1);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Emit(EngineEventKind.Log, $"引擎循环异常：{ex.Message}", severity: 2);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.TickSeconds)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>一轮评估：共享探针 → 逐规则状态机 → 触发动作。</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!GlobalEnabled) return;
        var now = _clock.Now;

        if (_startedAt != null && (now - _startedAt.Value).TotalSeconds < _options.StartupGraceSeconds)
        {
            EmitOnceGrace(now);
            return;
        }
        if (_settleUntil != null && now < _settleUntil.Value)
        {
            EmitOnceSettle(now);
            return;
        }

        List<AutomationRule> due;
        lock (_lock)
        {
            due = _rules.Where(r => r.Enabled && IsDue(r, now)).ToList();
        }
        if (due.Count == 0) return;

        // 共享探针
        var requests = new HashSet<ProbeRequest>(new ProbeRequestKeyComparer());
        foreach (var r in due)
            r.Condition.CollectProbeRequests(requests);
        var snapshot = await _probes.ProbeAsync(requests, ct);

        foreach (var r in due)
            await EvaluateAndMaybeTriggerAsync(r, snapshot, now, ct);
        SaveStates();
    }

    private bool _graceNotified;
    private bool _settleNotified;
    private void EmitOnceGrace(DateTime now)
    {
        if (_graceNotified) return;
        _graceNotified = true;
        int remaining = _startedAt == null ? _options.StartupGraceSeconds :
            Math.Max(0, (int)Math.Ceiling(_options.StartupGraceSeconds - (now - _startedAt.Value).TotalSeconds));
        Emit(EngineEventKind.Log, $"启动宽限期：暂不评估（等网络稳定，约剩 {remaining} 秒）。", severity: 1);
    }
    private void EmitOnceSettle(DateTime now)
    {
        if (_settleNotified) return;
        _settleNotified = true;
        int remaining = _settleUntil == null ? 0 : Math.Max(0, (int)Math.Ceiling((_settleUntil.Value - now).TotalSeconds));
        Emit(EngineEventKind.Log, $"网络稳定等待期：暂不评估（约剩 {remaining} 秒）。", severity: 1);
    }

    private bool IsDue(AutomationRule rule, DateTime now)
    {
        if (!_states.TryGetValue(rule.Id, out var s)) return true;
        return s.LastEvaluatedAt == null || (now - s.LastEvaluatedAt.Value).TotalMinutes >= rule.IntervalMinutes;
    }

    // ---------------- 规则评估 ----------------

    private async Task EvaluateAndMaybeTriggerAsync(AutomationRule rule, ProbeSnapshot snapshot, DateTime now, CancellationToken ct)
    {
        var state = GetOrCreateState(rule);
        state.LastEvaluatedAt = now;

        var ctx = new ConditionContext(snapshot, _profiles, id => IsSuppressed(id));
        TriState tri;
        string summary;
        try
        {
            tri = rule.Condition.Evaluate(ctx);
            summary = rule.Condition.Describe();
        }
        catch (Exception ex)
        {
            tri = TriState.Unknown;
            summary = $"条件求值异常：{ex.Message}";
        }

        state.LastResult = tri switch
        {
            TriState.True => "满足",
            TriState.False => "不满足",
            _ => "未知"
        };
        state.LastConditionSummary = summary;
        state.LastConditionTrue = tri == TriState.True;
        state.LastEvaluatedResultTime = now;

        // 滑窗：DoNotCount 的 Unknown 不占窗口槽位（既不匹配也不烧掉槽位）
        if (rule.RecentWindowSize > 0)
        {
            bool skipUnknown = tri == TriState.Unknown && rule.UnknownPolicy == UnknownPolicy.DoNotCount;
            if (!skipUnknown)
            {
                bool windowValue = tri == TriState.True
                    || (tri == TriState.Unknown && rule.UnknownPolicy == UnknownPolicy.CountAsSuccess);
                state.RecentWindow.Add(windowValue);
                while (state.RecentWindow.Count > rule.RecentWindowSize)
                    state.RecentWindow.RemoveAt(0);
            }
        }

        // 状态机更新
        switch (tri)
        {
            case TriState.True:
                state.ConsecutiveMatches++;
                state.FirstSatisfiedAt ??= now;
                break;
            case TriState.False:
                // 恢复：清零并重新武装（滑窗模式下保留窗口，滑窗本身容忍偶发）
                state.ConsecutiveMatches = 0;
                state.FirstSatisfiedAt = null;
                state.Armed = true;
                state.LastRecoveredTime = now;
                if (rule.RecentWindowSize <= 0)
                    state.RecentWindow.Clear();
                break;
            case TriState.Unknown:
                if (rule.UnknownPolicy == UnknownPolicy.CountAsFailure)
                {
                    state.ConsecutiveMatches = 0;
                    state.FirstSatisfiedAt = null;
                    state.Armed = true;
                    state.LastRecoveredTime = now;
                }
                else if (rule.UnknownPolicy == UnknownPolicy.CountAsSuccess)
                {
                    state.ConsecutiveMatches++;
                    state.FirstSatisfiedAt ??= now;
                }
                else
                {
                    // DoNotCount：不累计、不重置；仅告警限流（在触发判定外单独提示）
                }
                break;
        }

        // 触发判定
        bool matched = IsMatched(rule, state, now);
        bool budgetOk = CheckBudget(rule, state, now);
        bool cooldownOk = state.LastTriggerTime == null || (now - state.LastTriggerTime.Value).TotalSeconds >= rule.CooldownSeconds;

        // 重新武装超时：触发后若条件持续满足（如重启太早、故障未恢复）超过 RearmTimeoutSeconds，
        // 自动重新武装一次允许再次触发（配合预算上限防止无限循环）
        bool rearmed = !state.Armed && rule.RearmTimeoutSeconds > 0 && state.LastTriggerTime != null
                    && (now - state.LastTriggerTime.Value).TotalSeconds >= rule.RearmTimeoutSeconds;
        if (rearmed)
        {
            state.Armed = true;
            Emit(EngineEventKind.Log, $"规则「{rule.Name}」已超过重新武装超时（{rule.RearmTimeoutSeconds} 秒未恢复），允许再次触发。", rule.Id, rule.Name, 1);
        }

        bool actionsConfigured = rule.Actions.Count > 0;
        bool canTrigger = matched && (state.Armed || rearmed) && budgetOk && cooldownOk && actionsConfigured;

        string decision = canTrigger
            ? "已达到触发条件"
            : tri switch
            {
                TriState.Unknown => "条件未知，按 Unknown 策略不触发",
                TriState.False => "条件不满足",
                _ when !matched => $"条件满足但尚未达到门槛（{state.ConsecutiveMatches}/{Math.Max(1, rule.ConsecutiveMatches)}）",
                _ when !state.Armed && !rearmed => "已触发过，等待条件恢复后重新武装",
                _ when !budgetOk => "预算已用尽",
                _ when !cooldownOk => "触发冷却中",
                _ when !actionsConfigured => "规则没有配置动作",
                _ => "安全策略阻止触发",
            };
        bool decisionChanged = !string.Equals(state.LastDecision, decision, StringComparison.Ordinal);
        state.LastDecision = decision;
        if (decisionChanged)
        {
            // 只把状态转换写入持久审计，既能还原故障过程，也避免每分钟无限刷盘。
            _audit.Write(new AuditEntry
            {
                Timestamp = now,
                RuleId = rule.Id,
                RuleName = rule.Name,
                Kind = AuditEventKind.Evaluate,
                Detail = decision,
                ConditionSummary = summary,
                Evidence = BuildEvidenceSummary(snapshot),
            });
        }

        // 当前会话的运行日志每次实际评估都记录，让用户能确认调度器确实在工作；
        // UI 自身限制为最近 500 条，不会无限增长。
        Emit(EngineEventKind.Log,
            $"评估：{state.LastResult}；{decision}。连续 {state.ConsecutiveMatches}/{Math.Max(1, rule.ConsecutiveMatches)}。",
            rule.Id, rule.Name, canTrigger ? 1 : (tri == TriState.Unknown ? 1 : 0));

        WriteEvaluateAudit(rule, state, tri, summary, snapshot);

        if (!canTrigger)
        {
            EmitRuleState(rule.Id);
            return;
        }

        await TriggerAsync(rule, state, now, snapshot, ct);
    }

    private static bool IsMatched(AutomationRule rule, RuleRuntimeState state, DateTime now)
    {
        if (rule.RecentWindowSize > 0)
        {
            if (state.RecentWindow.Count < rule.RecentWindowSize) return false;
            int trues = state.RecentWindow.Count(v => v);
            return trues >= Math.Max(1, rule.RecentMinMatches);
        }
        bool consecutiveOk = state.ConsecutiveMatches >= Math.Max(1, rule.ConsecutiveMatches);
        bool sustainOk = state.FirstSatisfiedAt == null || (now - state.FirstSatisfiedAt.Value).TotalSeconds >= rule.SustainSeconds;
        return consecutiveOk && sustainOk;
    }

    private bool CheckBudget(AutomationRule rule, RuleRuntimeState state, DateTime now)
    {
        // 滚动窗口：只统计最近 1 小时 / 24 小时内的实际执行次数（而非整点/自然日清零）
        TrimBudgetTimes(state);

        int hourCount = state.RecentTriggerTimes.Count(t => now - t <= TimeSpan.FromHours(1));
        int dayCount = state.RecentTriggerTimes.Count(t => now - t <= TimeSpan.FromDays(1));
        state.HourTriggerCount = hourCount;
        state.DayTriggerCount = dayCount;
        bool hourHit = rule.Budget.PerHour > 0 && hourCount >= rule.Budget.PerHour;
        bool dayHit = rule.Budget.PerDay > 0 && dayCount >= rule.Budget.PerDay;
        if (hourHit || dayHit)
        {
            // 边沿触发：只在进入超限状态时记录一次，避免每分钟刷 Danger 日志
            if (!state.DisabledByBudget)
            {
                state.DisabledByBudget = true;
                _audit.Write(new AuditEntry
                {
                    Timestamp = now, RuleId = rule.Id, RuleName = rule.Name, CorrelationId = "",
                    Kind = AuditEventKind.BudgetHit,
                    Detail = hourHit ? $"小时预算 {rule.Budget.PerHour} 次已用尽" : $"日预算 {rule.Budget.PerDay} 次已用尽",
                });
                Emit(EngineEventKind.Log, $"规则「{rule.Name}」已达预算上限，本窗口内不再触发。", rule.Id, rule.Name, 2);
            }
            return false;
        }
        state.DisabledByBudget = false;
        return true;
    }

    private void TrimBudgetTimes(RuleRuntimeState state)
    {
        // 只保留最近 24 小时（日预算统计需要），丢弃更早的；用 _clock.Now 保证虚拟时钟测试正确
        var cutoff = _clock.Now.AddHours(-24);
        while (state.RecentTriggerTimes.Count > 0 && state.RecentTriggerTimes[0] < cutoff)
            state.RecentTriggerTimes.RemoveAt(0);
    }

    private Task TriggerAsync(AutomationRule rule, RuleRuntimeState state, DateTime now, ProbeSnapshot snapshot, CancellationToken ct)
    {
        // 触发即解除武装并记下触发时间（防重入 + 冷却/重新武装超时基准）。
        // LifetimeTriggerCount 记"触发次数"（含 dry-run）；预算计数（RecentTriggerTimes）
        // 移到动作真正执行时（见 ExecuteActionChainAsync），避免"预算已扣、动作未跑"。
        state.Armed = false;
        state.LastTriggerTime = now;
        state.ConsecutiveMatches = 0;
        state.FirstSatisfiedAt = null;
        state.RecentWindow.Clear();
        state.LifetimeTriggerCount++;

        string correlationId = Guid.NewGuid().ToString("N");
        _audit.Write(new AuditEntry
        {
            Timestamp = now, CorrelationId = correlationId, RuleId = rule.Id, RuleName = rule.Name,
            Kind = AuditEventKind.Trigger,
            ConditionSummary = state.LastConditionSummary,
            Evidence = BuildEvidenceSummary(snapshot),
            Detail = rule.DryRun ? "条件满足（dry-run，不执行动作）" : "条件满足，开始执行动作链",
        });
        Emit(EngineEventKind.Log, $"规则「{rule.Name}」触发。", rule.Id, rule.Name, 1);

        if (rule.DryRun)
        {
            Emit(EngineEventKind.Log, $"规则「{rule.Name}」为 dry-run，仅记录不执行。", rule.Id, rule.Name, 1);
            EmitRuleState(rule.Id);
            return Task.CompletedTask;
        }

        // 动作链在后台执行（含前置延迟与 Wait/WaitUntil 等长动作），不阻塞本 tick 及其他规则的评估。
        // 全局串行由 _actionLock 保证；预算在动作真正执行时记账。
        Emit(EngineEventKind.Log, $"规则「{rule.Name}」动作链已入队（前置延迟 {rule.PreActionDelaySeconds} 秒）。", rule.Id, rule.Name, 0);
        EmitRuleState(rule.Id);

        // 使用本次评估传入的 token，而不是重新读取可被 Stop() 置空的 _cts；
        // 这样停止引擎时，已经入队但尚未开始的动作链也能按预期取消。
        var token = ct;
        var task = RunActionChainAsync(rule, correlationId, token);
        lock (_lock)
        {
            _actionTasks.Add(task);
            _ = task.ContinueWith(t =>
            {
                lock (_lock) _actionTasks.Remove(t);
            }, TaskScheduler.Default);
        }
        return Task.CompletedTask;
    }

    /// <summary>动作链后台执行入口：先前置延迟（锁外），再执行动作链并记账预算。</summary>
    private async Task RunActionChainAsync(AutomationRule rule, string correlationId, CancellationToken ct)
    {
        try
        {
            if (rule.PreActionDelaySeconds > 0)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(rule.PreActionDelaySeconds), ct); }
                catch (OperationCanceledException) { return; }
            }
            await ExecuteActionChainAsync(rule, correlationId, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Emit(EngineEventKind.Log, $"规则「{rule.Name}」动作链异常：{ex.Message}", rule.Id, rule.Name, 2);
        }
        finally
        {
            // 动作预算是在真正执行时记账，必须在后台动作链结束后立刻持久化并刷新 UI；
            // 不能只依赖下一次 tick/退出，否则异常退出会丢失“今日次数”。
            SaveStates();
            EmitRuleState(rule.Id);
        }
    }

    /// <summary>等待所有已入队的动作链完成（测试与停止时用）。</summary>
    public async Task WaitForIdleAsync(int timeoutMs = 15000)
    {
        Task[] pending;
        lock (_lock) pending = _actionTasks.ToArray();
        if (pending.Length == 0) return;
        var all = Task.WhenAll(pending);
        try { await Task.WhenAny(all, Task.Delay(timeoutMs)); } catch { }
    }

    private async Task ExecuteActionChainAsync(AutomationRule rule, string correlationId, CancellationToken ct)
    {
        // 全局串行：同一时刻最多执行一条规则的动作链
        await _actionLock.WaitAsync(ct);
        try
        {
            if (CircuitBreakerBlocked(rule))
            {
                _audit.Write(new AuditEntry
                {
                    Timestamp = _clock.Now, CorrelationId = correlationId, RuleId = rule.Id, RuleName = rule.Name,
                    Kind = AuditEventKind.CircuitBreaker,
                    Detail = $"全局熔断：{_options.GlobalCircuitWindowMinutes} 分钟内破坏性动作已达 {_options.GlobalCircuitMaxDestructive} 次，动作链已跳过。",
                });
                Emit(EngineEventKind.Log, $"规则「{rule.Name}」被全局熔断拦截，动作未执行。", rule.Id, rule.Name, 2);
                return;
            }

            // 真正要执行时记账（滚动预算窗口），而非触发时——避免前置延迟/退出时"预算已扣、动作未跑"
            var execState = GetState(rule.Id);
            if (execState != null)
            {
                execState.RecentTriggerTimes.Add(_clock.Now);
                TrimBudgetTimes(execState);
                execState.HourTriggerCount = execState.RecentTriggerTimes.Count(t => _clock.Now - t <= TimeSpan.FromHours(1));
                execState.DayTriggerCount = execState.RecentTriggerTimes.Count(t => _clock.Now - t <= TimeSpan.FromDays(1));
            }

            var ctx = new ActionContext
            {
                Shell = _shell,
                Profiles = _profiles,
                ProfileMutator = _profileMutator,
                Adapters = _adapters,
                Audit = _audit,
                CorrelationId = correlationId,
                RuleId = rule.Id,
                RuleName = rule.Name,
            };

            bool prevOk = true;
            bool prevExists = false;
            foreach (var action in rule.Actions)
            {
                ct.ThrowIfCancellationRequested();

                bool shouldRun = action.RunWhen switch
                {
                    RunWhenMode.Always => true,
                    RunWhenMode.OnlyOnPreviousFailure => prevExists && !prevOk,
                    RunWhenMode.OnlyOnPreviousSuccess => prevExists && prevOk,
                    _ => true
                };
                if (!shouldRun) continue;

                string preview = action.BuildCommandPreview(ctx) ?? "";
                _audit.Write(new AuditEntry
                {
                    Timestamp = _clock.Now, CorrelationId = correlationId, RuleId = rule.Id, RuleName = rule.Name,
                    Kind = AuditEventKind.ActionStart,
                    ActionType = action.GetType().Name,
                    ActionParams = Redactor.Redact(action.Describe()),
                    CommandPreview = preview,
                });

                ActionResult result;
                try
                {
                    result = await action.ExecuteAsync(ctx, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result = new ActionResult { Ok = false, Message = ex.Message, IsDestructive = IsDestructiveAction(action) };
                }

                // 抑制窗口：动作可指定时长（如 Renew 短、Restart 长），默认用引擎配置
                foreach (var aid in result.SuppressAdapterIds)
                {
                    int secs = result.SuppressDurationSeconds > 0 ? result.SuppressDurationSeconds : _options.SuppressionSeconds;
                    _suppressedUntil[aid] = _clock.Now.AddSeconds(secs);
                }

                if (result.IsDestructive)
                    _destructiveTimes.Add(_clock.Now);

                _audit.Write(new AuditEntry
                {
                    Timestamp = _clock.Now, CorrelationId = correlationId, RuleId = rule.Id, RuleName = rule.Name,
                    Kind = result.Ok ? AuditEventKind.ActionEnd : AuditEventKind.ActionFailed,
                    ActionType = action.GetType().Name,
                    ActionParams = Redactor.Redact(action.Describe()),
                    CommandPreview = preview,
                    Ok = result.Ok,
                    Output = result.Message,
                });
                Emit(EngineEventKind.Log,
                    $"规则「{rule.Name}」动作「{action.Describe()}」{(result.Ok ? "成功" : "失败")}：{Redactor.Redact(result.Message)}",
                    rule.Id, rule.Name, result.Ok ? 0 : 2);

                prevOk = result.Ok;
                prevExists = true;

                // 通知动作 → 抛出 Notify 事件，由 UI 层决定是否弹窗
                if (action is NotifyAction notify)
                {
                    EventRaised?.Invoke(new EngineEvent
                    {
                        Kind = EngineEventKind.Notify,
                        Message = $"{notify.Title}：{notify.Message}",
                        RuleId = rule.Id,
                        RuleName = rule.Name,
                        Popup = notify.Popup,
                        Severity = 1,
                    });
                }

                if (!result.Ok && action.OnFailure == OnFailureMode.Stop)
                {
                    Emit(EngineEventKind.Log, $"规则「{rule.Name}」动作失败且配置为 StopOnFailure，动作链已中止。", rule.Id, rule.Name, 2);
                    break;
                }
            }
        }
        finally
        {
            _actionLock.Release();
        }
    }

    private bool CircuitBreakerBlocked(AutomationRule rule)
    {
        var now = _clock.Now;
        var cutoff = now.AddMinutes(-_options.GlobalCircuitWindowMinutes);
        _destructiveTimes.RemoveAll(t => t < cutoff);
        return _destructiveTimes.Count >= _options.GlobalCircuitMaxDestructive;
    }

    private bool IsSuppressed(string adapterId)
        => _suppressedUntil.TryGetValue(adapterId, out var t) && _clock.Now < t;

    // ---------------- 公共操作 ----------------

    /// <summary>
    /// 立即探测指定规则（纯观察：不更新状态机计数、不执行动作）。
    ///
    /// 这个方法故意不等价于后台调度的一次 tick。它用于查看当前证据，
    /// 因而不会因为用户点了一次按钮就绕过“连续满足/冷却/预算”等安全门槛。
    /// 需要手动验证动作链时请使用 EvaluateAndTriggerNowAsync。
    /// </summary>
    public async Task<string> EvaluateNowAsync(string ruleId, CancellationToken ct = default)
    {
        var rule = _rules.FirstOrDefault(r => r.Id == ruleId);
        if (rule == null) return "规则不存在。";
        var requests = new HashSet<ProbeRequest>(new ProbeRequestKeyComparer());
        rule.Condition.CollectProbeRequests(requests);
        var snapshot = await _probes.ProbeAsync(requests, ct);
        var ctx = new ConditionContext(snapshot, _profiles, id => IsSuppressed(id));
        TriState tri;
        try { tri = rule.Condition.Evaluate(ctx); }
        catch { tri = TriState.Unknown; }
        string label = tri switch { TriState.True => "满足", TriState.False => "不满足", _ => "未知" };
        var state = GetState(rule.Id);
        string runtime = state == null
            ? "尚无后台运行状态"
            : $"连续满足 {state.ConsecutiveMatches}/{Math.Max(1, rule.ConsecutiveMatches)} 次，" +
              $"{(state.Armed ? "已武装" : "等待恢复")}";
        string gates = !GlobalEnabled
            ? "全局开关关闭"
            : !rule.Enabled
                ? "规则已禁用"
                : rule.DryRun
                    ? "dry-run（动作不会执行）"
                    : "自动执行模式";
        string detail = $"规则「{rule.Name}」当前：{label}（{rule.Condition.Describe()}）；" +
                        $"运行状态：{runtime}；{gates}。本按钮仅探测，不会执行动作。";
        _audit.Write(new AuditEntry
        {
            Timestamp = _clock.Now, RuleId = rule.Id, RuleName = rule.Name,
            Kind = AuditEventKind.Evaluate, Detail = detail,
            Evidence = BuildEvidenceSummary(snapshot),
        });
        Emit(EngineEventKind.Log, detail, rule.Id, rule.Name, tri == TriState.True ? 1 : 0);
        return detail;
    }

    /// <summary>
    /// 立即执行一次“手动调度评估”。它跳过启动宽限期、网络稳定等待期、
    /// 规则评估间隔和连续次数/持续时长门槛，但仍遵守全局开关、规则启用状态、
    /// dry-run、冷却、重新武装、预算和全局熔断等安全策略。
    ///
    /// 该入口用于验证动作链，不会因为一次 UI 点击而无条件执行破坏性动作：
    /// 条件必须在当前快照中为 True，且规则必须处于已武装状态。
    /// </summary>
    public async Task<string> EvaluateAndTriggerNowAsync(string ruleId, CancellationToken ct = default)
    {
        var rule = _rules.FirstOrDefault(r => r.Id == ruleId);
        if (rule == null) return "规则不存在。";
        if (!GlobalEnabled) return "未执行：全局自动化开关已关闭，请先开启。";
        if (!rule.Enabled) return "未执行：规则已禁用。";

        var requests = new HashSet<ProbeRequest>(new ProbeRequestKeyComparer());
        rule.Condition.CollectProbeRequests(requests);
        var snapshot = await _probes.ProbeAsync(requests, ct);
        var ctx = new ConditionContext(snapshot, _profiles, id => IsSuppressed(id));

        TriState tri;
        try { tri = rule.Condition.Evaluate(ctx); }
        catch { tri = TriState.Unknown; }

        var now = _clock.Now;
        var state = GetOrCreateState(rule);
        state.LastEvaluatedAt = now;
        state.LastEvaluatedResultTime = now;
        state.LastConditionSummary = rule.Condition.Describe();
        state.LastConditionTrue = tri == TriState.True;
        state.LastResult = tri switch
        {
            TriState.True => "满足",
            TriState.False => "不满足",
            _ => "未知",
        };

        string label = tri switch { TriState.True => "满足", TriState.False => "不满足", _ => "未知" };
        string detail = $"规则「{rule.Name}」手动评估：{label}（{rule.Condition.Describe()}）";
        _audit.Write(new AuditEntry
        {
            Timestamp = now,
            RuleId = rule.Id,
            RuleName = rule.Name,
            Kind = AuditEventKind.Evaluate,
            Detail = detail,
            Evidence = BuildEvidenceSummary(snapshot),
        });

        if (tri != TriState.True)
        {
            Emit(EngineEventKind.Log, detail + "；当前不会执行动作。", rule.Id, rule.Name, tri == TriState.Unknown ? 1 : 0);
            SaveStates();
            return detail + "；当前不会执行动作。";
        }

        if (!state.Armed)
        {
            string msg = detail + "；规则正在等待恢复，未执行。";
            Emit(EngineEventKind.Log, msg, rule.Id, rule.Name, 1);
            SaveStates();
            return msg;
        }

        if (state.LastTriggerTime != null &&
            (now - state.LastTriggerTime.Value).TotalSeconds < Math.Max(0, rule.CooldownSeconds))
        {
            var remaining = TimeSpan.FromSeconds(rule.CooldownSeconds) - (now - state.LastTriggerTime.Value);
            string msg = detail + $"；冷却中（还需 {Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds))} 秒），未执行。";
            Emit(EngineEventKind.Log, msg, rule.Id, rule.Name, 1);
            SaveStates();
            return msg;
        }

        if (!CheckBudget(rule, state, now))
        {
            string msg = detail + "；已达到预算上限，未执行。";
            Emit(EngineEventKind.Log, msg, rule.Id, rule.Name, 2);
            SaveStates();
            return msg;
        }

        if (rule.Actions.Count == 0)
        {
            string msg = detail + "；规则没有动作，未执行。";
            Emit(EngineEventKind.Log, msg, rule.Id, rule.Name, 1);
            SaveStates();
            return msg;
        }

        await TriggerAsync(rule, state, now, snapshot, ct);
        SaveStates();
        string result = rule.DryRun
            ? detail + "；规则为 dry-run，仅记录，未执行动作。"
            : detail + $"；已触发动作链（前置延迟 {Math.Max(0, rule.PreActionDelaySeconds)} 秒，请查看运行日志）。";
        return result;
    }

    /// <summary>清除指定规则的连续计数、冷却和重新武装状态，用于修改执行模式后重新测试。</summary>
    public void ResetRuntimeState(string ruleId)
    {
        lock (_lock)
        {
            var rule = _rules.FirstOrDefault(r => r.Id == ruleId);
            if (rule == null) return;
            _states[rule.Id] = NewState(rule, _clock.Now);
        }
        SaveStates();
        EmitRuleState(ruleId);
        Emit(EngineEventKind.Log, "已重置规则运行状态（计数/冷却/重新武装）。", ruleId,
            _rules.FirstOrDefault(r => r.Id == ruleId)?.Name, 0);
    }

    /// <summary>重新加载规则（UI 编辑/导入后调用）。哈希变化的状态会被重置。</summary>
    public void ReloadRules()
    {
        var loaded = _ruleStore.Load();
        lock (_lock)
        {
            _rules = loaded;
            var ids = _rules.Select(r => r.Id).ToHashSet();
            foreach (var k in _states.Keys.Where(k => !ids.Contains(k)).ToList())
                _states.TryRemove(k, out _);
            foreach (var r in _rules)
            {
                if (_states.TryGetValue(r.Id, out var st) && NeedsRuntimeReset(r, st))
                    _states[r.Id] = NewState(r, _clock.Now);
            }
        }
        Emit(EngineEventKind.Log, "规则已重新加载。", severity: 0);
    }

    public void SaveStates()
    {
        Dictionary<string, RuleRuntimeState> snapshot;
        lock (_lock) snapshot = _states.ToDictionary(kv => kv.Key, kv => kv.Value);
        try { _stateStore.Save(snapshot); }
        catch (Exception ex)
        {
            Emit(EngineEventKind.Log, $"保存运行时状态失败：{ex.Message}", severity: 2);
        }
    }

    public IReadOnlyList<AutomationRule> GetRules()
    {
        lock (_lock) return _rules.ToList();
    }

    public RuleRuntimeState? GetState(string ruleId)
        => _states.TryGetValue(ruleId, out var s) ? s : null;

    public void ResetBudget(string ruleId)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(ruleId, out var s))
            {
                s.RecentTriggerTimes.Clear();
                s.HourTriggerCount = 0;
                s.DayTriggerCount = 0;
                s.DisabledByBudget = false;
            }
        }
        SaveStates();
        EmitRuleState(ruleId);
    }

    // ---------------- 内部辅助 ----------------

    private RuleRuntimeState GetOrCreateState(AutomationRule rule)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(rule.Id, out var s))
            {
                s = NewState(rule, _clock.Now);
                _states[rule.Id] = s;
            }
            return s;
        }
    }

    private static bool NeedsRuntimeReset(AutomationRule rule, RuleRuntimeState state)
    {
        // 旧状态文件没有这两个字段：保守地重置一次，避免把旧的等待恢复/冷却
        // 状态带入新的执行模式；随后保存时会补齐字段。
        if (state.LastDryRun == null || state.LastEnabled == null) return true;
        if (state.LastDryRun.Value != rule.DryRun) return true;
        if (state.LastEnabled.Value == false && rule.Enabled) return true;
        if (state.LastEnabled.Value != rule.Enabled)
        {
            // 禁用本身不需要清空预算/审计状态，但记下新值，
            // 这样下一次重新启用时会进入上面的 reset 分支。
            state.LastEnabled = rule.Enabled;
        }
        return state.RuleHash != rule.ContentHash;
    }

    private static RuleRuntimeState NewState(AutomationRule rule, DateTime now) => new()
    {
        RuleId = rule.Id,
        RuleHash = rule.ContentHash,
        LastDryRun = rule.DryRun,
        LastEnabled = rule.Enabled,
        Armed = true,
    };

    private void WriteEvaluateAudit(AutomationRule rule, RuleRuntimeState state, TriState tri, string summary, ProbeSnapshot snapshot)
    {
        // 评估日志不每条都写审计（避免刷盘），仅当状态变化或触发时由 Trigger 写。
        // 此处仅保留给 UI 事件。
        EmitRuleState(rule.Id);
    }

    private static string BuildEvidenceSummary(ProbeSnapshot snapshot)
    {
        var parts = new List<string>();
        foreach (var (k, v) in snapshot.Results)
            parts.Add($"{k}={v.State}({v.Reason})");
        return parts.Count == 0 ? "无探针" : string.Join("; ", parts.Take(20));
    }

    private static bool IsDestructiveAction(ActionBase a)
        => a is RestartAdapterAction or DeleteNetworkProfileAction or RenameNetworkProfileAction or MergeNetworkProfilesAction;

    private void Emit(EngineEventKind kind, string message, string? ruleId = null, string? ruleName = null, int severity = 0)
        => EventRaised?.Invoke(new EngineEvent { Kind = kind, Message = message, RuleId = ruleId, RuleName = ruleName, Severity = severity });

    private void EmitRuleState(string ruleId)
        => EventRaised?.Invoke(new EngineEvent { Kind = EngineEventKind.RuleStateChanged, Message = ruleId, RuleId = ruleId });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        // 不再 Dispose _actionLock：退出进程前可能仍有动作在收尾（ProcessRunner 不响应取消），
        // 释放它会让后续 WaitAsync 抛 ObjectDisposedException。进程退出会回收资源。
    }
}

/// <summary>ProbeRequest 按 Key 去重的比较器。</summary>
public sealed class ProbeRequestKeyComparer : IEqualityComparer<ProbeRequest>
{
    public bool Equals(ProbeRequest? x, ProbeRequest? y)
        => string.Equals(x?.Key, y?.Key, StringComparison.Ordinal);
    public int GetHashCode(ProbeRequest obj) => obj.Key.GetHashCode();
}
