using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WinNetManager.Core.Abstractions;
using WinNetManager.Core.Engine;
using WinNetManager.Core.Models;
using WinNetManager.Core.Storage;
using WinNetManager.Services;
using WinNetManager.Services.Automation;

namespace WinNetManager.Views;

public partial class AutomationTab : UserControl
{
    private readonly ObservableCollection<RuleRowViewModel> _rules = new();
    private readonly ObservableCollection<LogEntryViewModel> _logs = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private bool _loading;
    private bool _historyLoaded;

    public AutomationTab()
    {
        InitializeComponent();
        RuleGrid.ItemsSource = _rules;
        LogList.ItemsSource = _logs;

        AutomationHost.Initialize();
        AutomationHost.Engine.EventRaised += OnEngineEvent;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            AutomationHost.Engine.ReloadRules();
            AutomationHost.Engine.GlobalEnabled = _settings.AutomationGlobalEnabled;
            TglGlobal.IsChecked = AutomationHost.Engine.GlobalEnabled;
            UpdateEngineStateText();
            RefreshRules();
        }
        finally { _loading = false; }

        if (!_historyLoaded)
        {
            LoadRecentAuditHistory();
            ReportRuleConfigurationWarnings();
            _historyLoaded = true;
        }

        // 引擎可能已在 App.OnStartup 中启动；Start() 本身幂等，
        // 这里再确保一次，避免窗口/标签页尚未创建时错过启动。
        if (AutomationHost.Engine.GlobalEnabled && !AutomationHost.Engine.IsRunning)
            AutomationHost.Engine.Start();

        int enabled = AutomationHost.Engine.GetRules().Count(r => r.Enabled);
        int executable = AutomationHost.Engine.GetRules().Count(r => r.Enabled && !r.DryRun && r.Actions.Count > 0);
        SetStatus(AutomationHost.Engine.GlobalEnabled
            ? $"自动化引擎已开启：{enabled} 条启用规则，其中 {executable} 条为自动执行模式。"
            : "自动化引擎已关闭；请开启顶部总开关，且确认规则已启用并关闭 dry-run。");
    }

    private static string GetRuleStorePath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinNetManager", "automation_rules.json");

    private void OnEngineEvent(EngineEvent ev)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnEngineEvent(ev));
            return;
        }
        switch (ev.Kind)
        {
            case EngineEventKind.Log:
                AddLog(ev);
                break;
            case EngineEventKind.RuleStateChanged:
                if (ev.RuleId != null)
                {
                    var row = _rules.FirstOrDefault(r => r.Rule.Id == ev.RuleId);
                    row?.Refresh(AutomationHost.Engine.GetState(ev.RuleId));
                }
                break;
            case EngineEventKind.EngineStateChanged:
                UpdateEngineStateText();
                break;
            case EngineEventKind.Notify:
                AddLog(ev);
                if (ev.Popup)
                {
                    Dispatcher.InvokeAsync(() =>
                        CopyableMessageBox.Show(ev.Message, "自动化监控", MessageBoxImage.Warning));
                }
                break;
        }
    }

    private void AddLog(EngineEvent ev)
    {
        var tag = ev.RuleName != null ? $"[{ev.RuleName}]" : "引擎";
        _logs.Add(new LogEntryViewModel
        {
            Timestamp = ev.Timestamp,
            Tag = tag,
            Message = ev.Message,
            Severity = ev.Severity,
        });
        while (_logs.Count > 500) _logs.RemoveAt(0);
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private void LoadRecentAuditHistory()
    {
        // UI 日志以前只存在于内存，重新启动后看不到历史。现在从 JSONL 审计恢复最近记录。
        _logs.Clear();
        foreach (var entry in AutomationHost.AuditLog.ReadRecent(200))
        {
            string message = entry.Kind switch
            {
                AuditEventKind.ActionStart => $"开始动作 {entry.ActionType}：{entry.ActionParams}",
                AuditEventKind.ActionEnd => $"动作成功 {entry.ActionType}：{entry.Output}",
                AuditEventKind.ActionFailed => $"动作失败 {entry.ActionType}：{entry.Output}",
                AuditEventKind.Trigger => string.IsNullOrWhiteSpace(entry.Detail) ? "规则已触发。" : entry.Detail,
                _ => string.IsNullOrWhiteSpace(entry.Detail) ? entry.Kind.ToString() : entry.Detail,
            };
            _logs.Add(new LogEntryViewModel
            {
                Timestamp = entry.Timestamp,
                Tag = string.IsNullOrWhiteSpace(entry.RuleName) ? "[历史/引擎]" : $"[历史/{entry.RuleName}]",
                Message = message,
                Severity = entry.Kind is AuditEventKind.ActionFailed or AuditEventKind.BudgetHit or AuditEventKind.CircuitBreaker ? 2
                    : entry.Kind == AuditEventKind.Trigger ? 1 : 0,
            });
        }
        if (_logs.Count > 0)
            LogList.ScrollIntoView(_logs[^1]);
    }

    private void ReportRuleConfigurationWarnings()
    {
        foreach (var rule in AutomationHost.Engine.GetRules())
        {
            foreach (var warning in FindConditionWarnings(rule.Condition))
            {
                AddLog(new EngineEvent
                {
                    Kind = EngineEventKind.Log,
                    RuleId = rule.Id,
                    RuleName = rule.Name,
                    Severity = 1,
                    Message = "配置警告：" + warning,
                });
            }
        }
    }

    private static IEnumerable<string> FindConditionWarnings(ConditionGroup group)
    {
        if (group.Operator == LogicOperator.And)
        {
            var addressConditions = group.Children.OfType<AdapterHasAddressCondition>().ToList();
            foreach (var ping in group.Children.OfType<PingFailCondition>().Where(p => p.TreatMissingAddressAsFailure))
            {
                if (addressConditions.Any(a =>
                        string.Equals(a.AdapterId, ping.AdapterId, StringComparison.OrdinalIgnoreCase) &&
                        a.Family == ping.Family))
                {
                    yield return $"同时要求“网卡有 {ping.Family} 地址”和“{ping.Family} ping 失败”。" +
                                 "如果掉线表现为地址丢失，前一条件为假，整条 AND 规则永远不会触发；" +
                                 "自愈规则通常应删除“网卡有地址”条件。";
                }
            }
        }

        foreach (var child in group.Children.OfType<ConditionGroup>())
            foreach (var warning in FindConditionWarnings(child))
                yield return warning;
    }

    private void UpdateEngineStateText()
    {
        var engine = AutomationHost.Engine;
        TbkEngineState.Text = engine.GlobalEnabled
            ? (engine.IsRunning ? "● 运行中" : "● 已开启（未启动调度）")
            : "○ 已关闭";
    }

    // ---------------- 规则列表 ----------------

    private void RefreshRules()
    {
        _loading = true;
        try
        {
            _rules.Clear();
            var engine = AutomationHost.Engine;
            foreach (var rule in engine.GetRules().OrderBy(r => r.Name))
            {
                var row = new RuleRowViewModel(rule);
                row.Refresh(engine.GetState(rule.Id));
                _rules.Add(row);
            }
        }
        finally { _loading = false; }
        EmptyState.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private RuleRowViewModel? GetSelectedRow()
        => RuleGrid.SelectedItem as RuleRowViewModel;

    private void SaveAndReload()
    {
        AutomationHost.RuleStore.Save(AutomationHost.Engine.GetRules());
        AutomationHost.Engine.ReloadRules();
        RefreshRules();
    }

    /// <summary>把一条规则持久化（新增或按 Id 替换），再重新加载。</summary>
    private void PersistAddOrReplace(AutomationRule rule)
    {
        var rules = AutomationHost.Engine.GetRules().ToList();
        int idx = rules.FindIndex(r => r.Id == rule.Id);
        if (idx >= 0) rules[idx] = rule;
        else rules.Add(rule);
        AutomationHost.RuleStore.Save(rules);
        AutomationHost.Engine.ReloadRules();
        RefreshRules();
    }

    private async void BtnEvaluate_Click(object sender, RoutedEventArgs e)
    {
        var row = GetSelectedRow();
        if (row == null)
        {
            CopyableMessageBox.Show("请先选择一条规则。", "未选择", MessageBoxImage.Information);
            return;
        }
        SetStatus($"正在评估规则「{row.Rule.Name}」...");
        string result = await AutomationHost.Engine.EvaluateNowAsync(row.Rule.Id);
        SetStatus(result);
        CopyableMessageBox.Show(result, "评估结果",
            result.Contains("满足", StringComparison.Ordinal) ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void BtnEvaluateAndTrigger_Click(object sender, RoutedEventArgs e)
    {
        var row = GetSelectedRow();
        if (row == null)
        {
            CopyableMessageBox.Show("请先选择一条规则。", "未选择", MessageBoxImage.Information);
            return;
        }

        if (!row.Rule.DryRun)
        {
            var confirm = MessageBox.Show(
                $"将立即探测规则「{row.Rule.Name}」，如果当前条件满足，将按规则动作链执行。\n\n" +
                "这可能会 Renew/重启网卡、访问 URL 或修改网络配置文件。\n" +
                "仍会遵守全局开关、冷却、重新武装、预算和熔断策略。\n\n确定继续？",
                "确认测试动作", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        SetStatus($"正在测试规则「{row.Rule.Name}」...");
        string result;
        try
        {
            result = await AutomationHost.Engine.EvaluateAndTriggerNowAsync(row.Rule.Id);
        }
        catch (Exception ex)
        {
            result = $"测试动作失败：{ex.Message}";
        }
        row.Refresh(AutomationHost.Engine.GetState(row.Rule.Id));
        SetStatus(result);
        CopyableMessageBox.Show(result, "测试动作结果",
            result.Contains("已触发", StringComparison.Ordinal) || result.Contains("dry-run", StringComparison.Ordinal)
                ? MessageBoxImage.Information
                : MessageBoxImage.Warning);
    }

    private void BtnResetState_Click(object sender, RoutedEventArgs e)
    {
        var row = GetSelectedRow();
        if (row == null) return;
        AutomationHost.Engine.ResetRuntimeState(row.Rule.Id);
        row.Refresh(AutomationHost.Engine.GetState(row.Rule.Id));
        SetStatus($"已重置规则「{row.Rule.Name}」的运行状态。现在可以重新测试动作。");
    }

    private void BtnResetBudget_Click(object sender, RoutedEventArgs e)
    {
        var row = GetSelectedRow();
        if (row == null) return;
        AutomationHost.Engine.ResetBudget(row.Rule.Id);
        row.Refresh(AutomationHost.Engine.GetState(row.Rule.Id));
        SetStatus($"已清除规则「{row.Rule.Name}」的触发计数（预算已重置）。");
    }

    private void BtnToggleEnabled_Click(object sender, RoutedEventArgs e)
    {
        var row = GetSelectedRow();
        if (row == null) return;
        row.Rule.Enabled = !row.Rule.Enabled;
        SaveAndReload();
        SetStatus($"规则「{row.Rule.Name}」已{(row.Rule.Enabled ? "启用" : "禁用")}。");
    }

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RuleEditorWindow(null) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.ResultRule == null) return;
        PersistAddOrReplace(dlg.ResultRule);
        SetStatus("已保存新规则。");
    }

    private void BtnTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TemplatePickerWindow(AdapterCatalog.Get()) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.ResultRule == null) return;
        var editor = new RuleEditorWindow(dlg.ResultRule) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true || editor.ResultRule == null) return;
        PersistAddOrReplace(editor.ResultRule);
        SetStatus("已从模板创建规则（默认 dry-run + 禁用，请编辑确认后启用）。");
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e) => EditSelected();
    private void RuleGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => EditSelected();

    private void EditSelected()
    {
        var row = GetSelectedRow();
        if (row == null) return;
        var dlg = new RuleEditorWindow(row.Rule) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.ResultRule == null) return;
        PersistAddOrReplace(dlg.ResultRule);
        SetStatus($"已保存规则「{dlg.ResultRule.Name}」。");
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var row = GetSelectedRow();
        if (row == null)
        {
            CopyableMessageBox.Show("请先选择一条规则。", "未选择", MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"确定删除规则「{row.Rule.Name}」？\n\n删除后其历史触发状态一并清除。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var rules = AutomationHost.RuleStore.Load();
        rules.RemoveAll(r => r.Id == row.Rule.Id);
        AutomationHost.RuleStore.Save(rules);
        AutomationHost.Engine.ReloadRules();
        RefreshRules();
        SetStatus($"已删除规则「{row.Rule.Name}」。");
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择规则文件",
            Filter = "规则文件 (*.json)|*.json",
            DefaultExt = ".json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var store = new JsonRuleStore(dlg.FileName);
            var imported = store.Load();
            if (imported.Count == 0)
            {
                CopyableMessageBox.Show("文件中没有可导入的规则。", "导入结果", MessageBoxImage.Information);
                return;
            }
            // 导入规则强制 dry-run + 禁用 + 换新 Id，防止未经确认就自动执行、以及重复导入导致 Id 冲突
            int n = 0;
            foreach (var r in imported)
            {
                r.Id = Guid.NewGuid().ToString("N");
                r.DryRun = true;
                r.Enabled = false;
                r.Note = (string.IsNullOrWhiteSpace(r.Note) ? "" : r.Note + "；") + "导入副本（已重新生成 Id）";
                n++;
            }
            var existing = AutomationHost.RuleStore.Load();
            existing.AddRange(imported);
            AutomationHost.RuleStore.Save(existing);
            AutomationHost.Engine.ReloadRules();
            RefreshRules();
            CopyableMessageBox.Show($"已导入 {n} 条规则（强制 dry-run + 禁用，需手动启用）。", "导入完成", MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"导入失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出规则",
            FileName = $"automation_rules_{DateTime.Now:yyyyMMdd_HHmmss}.json",
            DefaultExt = ".json",
            Filter = "规则文件 (*.json)|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var store = new JsonRuleStore(dlg.FileName);
            store.Save(AutomationHost.Engine.GetRules());
            SetStatus($"已导出到 {dlg.FileName}");
            CopyableMessageBox.Show($"已导出 {AutomationHost.Engine.GetRules().Count} 条规则到：\n{dlg.FileName}\n\n（含 URL/代理等敏感字段，导出后请注意保管）", "导出完成", MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => _logs.Clear();

    // ---------------- 全局开关 ----------------

    private void TglGlobal_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AutomationHost.Engine.GlobalEnabled = true;
        _settings.AutomationGlobalEnabled = true;
        _settings.Save();
        AutomationHost.Engine.Start();
        UpdateEngineStateText();
        int enabled = AutomationHost.Engine.GetRules().Count(r => r.Enabled);
        int executable = AutomationHost.Engine.GetRules().Count(r => r.Enabled && !r.DryRun && r.Actions.Count > 0);
        SetStatus($"自动化引擎已开启：{enabled} 条启用规则，其中 {executable} 条为自动执行模式。");
    }

    private void TglGlobal_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AutomationHost.Engine.GlobalEnabled = false;
        _settings.AutomationGlobalEnabled = false;
        _settings.Save();
        AutomationHost.Engine.Stop();
        UpdateEngineStateText();
        SetStatus("自动化引擎已关闭；不会评估规则，也不会执行动作。");
    }

    private void SetStatus(string msg)
    {
        if (Window.GetWindow(this) is MainWindow mw) mw.SetStatus(msg);
    }
}

/// <summary>规则列表行视图模型。</summary>
public sealed class RuleRowViewModel : INotifyPropertyChanged
{
    public AutomationRule Rule { get; }
    private RuleRuntimeState? _state;

    public RuleRowViewModel(AutomationRule rule) => Rule = rule;

    public bool IsEnabled
    {
        get => Rule.Enabled;
        set
        {
            if (Rule.Enabled != value)
            {
                Rule.Enabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }
    }

    public string Name => Rule.Name;
    public string ModeText => !Rule.Enabled ? "停用" : (Rule.DryRun ? "演练" : "自动");
    public string ConditionSummary => Rule.Condition.Describe();
    public string ActionsSummary => string.Join(" → ", Rule.Actions.Select(a => a.Describe()));
    public int IntervalMinutes => Rule.IntervalMinutes;
    public int ConsecutiveMatches => Rule.ConsecutiveMatches;

    public string FullSummary =>
        $"条件：{ConditionSummary}\n动作：{ActionsSummary}\n间隔：{IntervalMinutes} 分钟，连续 {ConsecutiveMatches} 次" +
        (Rule.DryRun ? "，dry-run（仅记录）" : "") +
        (_state == null ? "" : $"\n上次判定：{_state.LastDecision}；上次结果：{_state.LastResult}") +
        (string.IsNullOrWhiteSpace(Rule.Note) ? "" : $"\n备注：{Rule.Note}");

    public string StatusText { get; private set; } = "未评估";
    public string StatusBrushKey { get; private set; } = "";
    public string LastEvaluated { get; private set; } = "—";
    public string LastTrigger { get; private set; } = "—";
    public int TodayCount { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh(RuleRuntimeState? state)
    {
        _state = state;
        if (!Rule.Enabled)
        {
            StatusText = "已禁用";
            StatusBrushKey = "";
        }
        else if (state == null)
        {
            StatusText = "未评估";
            StatusBrushKey = "";
        }
        else if (state.DisabledByBudget)
        {
            StatusText = "预算超限";
            StatusBrushKey = "Danger";
        }
        else if (!state.Armed)
        {
            StatusText = "等待恢复";
            StatusBrushKey = "Warn";
        }
        else if (state.LastTriggerTime != null &&
                 (DateTime.Now - state.LastTriggerTime.Value).TotalSeconds < Rule.CooldownSeconds)
        {
            StatusText = "冷却中";
            StatusBrushKey = "Warn";
        }
        else
        {
            StatusText = state.LastConditionTrue
                ? $"满足 {Math.Min(state.ConsecutiveMatches, Math.Max(1, Rule.ConsecutiveMatches))}/{Math.Max(1, Rule.ConsecutiveMatches)}"
                : (state.LastResult == "未知" ? "未知" : "正常");
            StatusBrushKey = state.LastConditionTrue ? "Warn" : "Ok";
        }

        LastEvaluated = state?.LastEvaluatedResultTime?.ToString("MM-dd HH:mm") ?? "—";
        LastTrigger = state?.LastTriggerTime?.ToString("MM-dd HH:mm") ?? "—";
        TodayCount = state?.DayTriggerCount ?? 0;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrushKey)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastEvaluated)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastTrigger)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TodayCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullSummary)));
    }
}

/// <summary>日志行视图模型。</summary>
public sealed class LogEntryViewModel
{
    public DateTime Timestamp { get; init; }
    public string Tag { get; init; } = "";
    public string Message { get; init; } = "";
    public int Severity { get; init; }
}
