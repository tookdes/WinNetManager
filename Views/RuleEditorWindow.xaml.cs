using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using WinNetManager.Core.Models;

namespace WinNetManager.Views;

public partial class RuleEditorWindow : Window
{
    public IReadOnlyList<AdapterOption> Adapters { get; }
    /// <summary>HTTP 出口网卡选项（含"(默认路由)"空项，便于清空回退到系统默认路由）。</summary>
    public IReadOnlyList<AdapterOption> ViaAdapters { get; }
    private readonly ObservableCollection<ConditionRowViewModel> _conditions = new();
    private readonly ObservableCollection<ActionRowViewModel> _actions = new();
    private readonly AutomationRule? _original;

    public RuleEditorWindow(AutomationRule? rule)
    {
        InitializeComponent();
        Adapters = AdapterCatalog.Get();
        ViaAdapters = new List<AdapterOption> { new() { Id = "", Name = "(默认路由)" } }.Concat(Adapters).ToList();
        DataContext = this;
        ConditionList.ItemsSource = _conditions;
        ActionList.ItemsSource = _actions;
        _original = rule;
        LoadRule(rule);
    }

    public AutomationRule? ResultRule { get; private set; }

    private void LoadRule(AutomationRule? rule)
    {
        var r = rule ?? new AutomationRule { Name = "新规则" };
        TxtName.Text = r.Name;
        ChkEnabled.IsChecked = r.Enabled;
        ChkDryRun.IsChecked = r.DryRun;
        TxtInterval.Text = r.IntervalMinutes.ToString();
        TxtConsecutive.Text = r.ConsecutiveMatches.ToString();
        TxtSustain.Text = r.SustainSeconds.ToString();
        TxtWindowSize.Text = r.RecentWindowSize.ToString();
        TxtWindowMin.Text = r.RecentMinMatches.ToString();
        TxtCooldown.Text = r.CooldownSeconds.ToString();
        TxtPreDelay.Text = r.PreActionDelaySeconds.ToString();
        TxtBudgetHour.Text = r.Budget.PerHour.ToString();
        TxtBudgetDay.Text = r.Budget.PerDay.ToString();

        SetComboTag(CmbConditionOperator, r.Condition.Operator == LogicOperator.And ? "And" : "Or");
        _conditions.Clear();
        foreach (var c in r.Condition.Children)
            _conditions.Add(ConditionRowViewModel.From(c));
        _actions.Clear();
        foreach (var a in r.Actions)
            _actions.Add(ActionRowViewModel.From(a));
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        ResultRule = null;
        DialogResult = false;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var rule = _original ?? new AutomationRule();
        rule.Name = TxtName.Text.Trim();
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            MessageBox.Show("请填写规则名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        rule.Enabled = ChkEnabled.IsChecked == true;
        rule.DryRun = ChkDryRun.IsChecked == true;
        rule.IntervalMinutes = ParseInt(TxtInterval.Text, 1, 1, 1440);
        rule.ConsecutiveMatches = ParseInt(TxtConsecutive.Text, 3, 1, 100);
        rule.SustainSeconds = ParseInt(TxtSustain.Text, 60, 0, 86400);
        rule.RecentWindowSize = ParseInt(TxtWindowSize.Text, 0, 0, 100);
        rule.RecentMinMatches = ParseInt(TxtWindowMin.Text, 0, 0, 100);
        rule.CooldownSeconds = ParseInt(TxtCooldown.Text, 300, 0, 86400);
        rule.PreActionDelaySeconds = ParseInt(TxtPreDelay.Text, 30, 0, 3600);
        rule.Budget.PerHour = ParseInt(TxtBudgetHour.Text, 3, 0, 1000);
        rule.Budget.PerDay = ParseInt(TxtBudgetDay.Text, 12, 0, 10000);

        rule.Condition = new ConditionGroup
        {
            Operator = GetComboTag(CmbConditionOperator) == "Or" ? LogicOperator.Or : LogicOperator.And,
            Children = _conditions.Select(c => c.ToCondition()).ToList(),
        };
        rule.Actions = _actions.Select(a => a.ToAction()).ToList();

        if (rule.Condition.Children.Count == 0)
        {
            MessageBox.Show("请至少添加一个条件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ResultRule = rule;
        DialogResult = true;
    }

    // ---------------- 条件 ----------------

    private void BtnAddCondition_Click(object sender, RoutedEventArgs e)
        => _conditions.Add(new ConditionRowViewModel());

    private void BtnRemoveCondition_Click(object sender, RoutedEventArgs e)
    {
        if (ConditionList.SelectedItem is ConditionRowViewModel c)
            _conditions.Remove(c);
    }

    // ---------------- 动作 ----------------

    private void BtnAddAction_Click(object sender, RoutedEventArgs e)
        => _actions.Add(new ActionRowViewModel());

    private void BtnRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (ActionList.SelectedItem is ActionRowViewModel a)
            _actions.Remove(a);
    }

    private void BtnMoveActionUp_Click(object sender, RoutedEventArgs e)
    {
        if (ActionList.SelectedItem is not ActionRowViewModel a) return;
        int i = _actions.IndexOf(a);
        if (i <= 0) return;
        _actions.Move(i, i - 1);
        ActionList.SelectedItem = a;
    }

    private void BtnMoveActionDown_Click(object sender, RoutedEventArgs e)
    {
        if (ActionList.SelectedItem is not ActionRowViewModel a) return;
        int i = _actions.IndexOf(a);
        if (i < 0 || i >= _actions.Count - 1) return;
        _actions.Move(i, i + 1);
        ActionList.SelectedItem = a;
    }

    // ---------------- 模板 ----------------

    private void BtnTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TemplatePickerWindow(Adapters) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ResultRule == null) return;
        LoadRule(dlg.ResultRule);
    }

    // ---------------- 工具 ----------------

    private static int ParseInt(string s, int fallback, int min, int max)
    {
        if (!int.TryParse(s.Trim(), out int v)) return fallback;
        return Math.Clamp(v, min, max);
    }

    private static string? GetComboTag(System.Windows.Controls.ComboBox c)
        => (c.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;

    private static void SetComboTag(System.Windows.Controls.ComboBox c, string tag)
    {
        foreach (var item in c.Items)
        {
            if (item is System.Windows.Controls.ComboBoxItem ci && (ci.Tag as string) == tag)
            {
                c.SelectedItem = item;
                return;
            }
        }
    }
}

/// <summary>网卡下拉选项。</summary>
public sealed class AdapterOption
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
}

public static class AdapterCatalog
{
    public static List<AdapterOption> Get()
    {
        var list = new List<AdapterOption>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                list.Add(new AdapterOption { Id = ni.Id, Name = ni.Name, Description = ni.Description ?? "" });
            }
        }
        catch { }
        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>条件行视图模型。</summary>
public sealed class ConditionRowViewModel : INotifyPropertyChanged
{
    private string _type = "pingFail";
    private string _adapterId = "";
    private string _family = "IPv6";
    private string _targets = "";
    private string _mode = "All";
    private string _timeoutMs = "3000";
    private string _name = "";
    private string _newerName = "";
    private string _olderName = "";

    public string Type { get => _type; set { _type = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Type))); } }
    public string AdapterId { get => _adapterId; set { _adapterId = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdapterId))); } }
    public string Family { get => _family; set { _family = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Family))); } }
    public string Targets { get => _targets; set { _targets = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Targets))); } }
    public string Mode { get => _mode; set { _mode = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mode))); } }
    public string TimeoutMs { get => _timeoutMs; set { _timeoutMs = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeoutMs))); } }
    public string Name { get => _name; set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); } }
    public string NewerName { get => _newerName; set { _newerName = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NewerName))); } }
    public string OlderName { get => _olderName; set { _olderName = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OlderName))); } }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ConditionBase ToCondition()
    {
        switch (Type)
        {
            case "pingFailV4":
                return new PingFailCondition { AdapterId = AdapterId, Family = AddressFamilyKind.IPv4, Targets = ParseTargets(), Mode = ParseMode(), TimeoutMs = ParseInt(TimeoutMs, 3000) };
            case "pingOk":
                return new PingSucceedsCondition { AdapterId = AdapterId, Family = AddressFamilyKind.IPv6, Targets = ParseTargets(), Mode = ParseMode(), TimeoutMs = ParseInt(TimeoutMs, 3000) };
            case "pingOkV4":
                return new PingSucceedsCondition { AdapterId = AdapterId, Family = AddressFamilyKind.IPv4, Targets = ParseTargets(), Mode = ParseMode(), TimeoutMs = ParseInt(TimeoutMs, 3000) };
            case "adapterUp":
                return new AdapterUpCondition { AdapterId = AdapterId };
            case "hasAddr":
                return new AdapterHasAddressCondition { AdapterId = AdapterId, Family = ParseFamily() };
            case "profileExists":
                return new ProfileExistsCondition { Name = Name.Trim() };
            case "profileAfter":
                return new ProfileLastConnectedAfterCondition { NewerName = NewerName.Trim(), OlderName = OlderName.Trim() };
            default: // pingFail
                return new PingFailCondition { AdapterId = AdapterId, Family = AddressFamilyKind.IPv6, Targets = ParseTargets(), Mode = ParseMode(), TimeoutMs = ParseInt(TimeoutMs, 3000) };
        }
    }

    public static ConditionRowViewModel From(ConditionBase c)
    {
        var vm = new ConditionRowViewModel();
        switch (c)
        {
            case PingFailCondition p:
                vm.Type = p.Family == AddressFamilyKind.IPv6 ? "pingFail" : "pingFailV4";
                vm.AdapterId = p.AdapterId;
                vm.Targets = string.Join(", ", p.Targets);
                vm.Mode = p.Mode.ToString();
                vm.TimeoutMs = p.TimeoutMs.ToString();
                break;
            case PingSucceedsCondition p:
                vm.Type = p.Family == AddressFamilyKind.IPv6 ? "pingOk" : "pingOkV4";
                vm.AdapterId = p.AdapterId;
                vm.Targets = string.Join(", ", p.Targets);
                vm.Mode = p.Mode.ToString();
                vm.TimeoutMs = p.TimeoutMs.ToString();
                break;
            case AdapterUpCondition a:
                vm.Type = "adapterUp";
                vm.AdapterId = a.AdapterId;
                break;
            case AdapterHasAddressCondition a:
                vm.Type = "hasAddr";
                vm.AdapterId = a.AdapterId;
                vm.Family = a.Family.ToString();
                break;
            case ProfileExistsCondition p:
                vm.Type = "profileExists";
                vm.Name = p.Name;
                break;
            case ProfileLastConnectedAfterCondition p:
                vm.Type = "profileAfter";
                vm.NewerName = p.NewerName;
                vm.OlderName = p.OlderName;
                break;
            default:
                vm.Type = "pingFail";
                break;
        }
        return vm;
    }

    private List<string> ParseTargets()
        => Targets.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private PingAggregateMode ParseMode()
        => Mode == "Any" ? PingAggregateMode.Any : PingAggregateMode.All;

    private AddressFamilyKind ParseFamily()
        => Family == "IPv4" ? AddressFamilyKind.IPv4 : AddressFamilyKind.IPv6;

    private static int ParseInt(string s, int fallback) => int.TryParse(s.Trim(), out int v) ? v : fallback;
}

/// <summary>动作行视图模型。</summary>
public sealed class ActionRowViewModel : INotifyPropertyChanged
{
    private string _type = "notify";
    private string _adapterId = "";
    private string _family = "IPv6";
    private string _method = "GET";
    private string _url = "";
    private string _viaAdapterId = "";
    private string _viaFamily = "IPv4";
    private string _socksProxy = "";
    private string _resolveHost = "";
    private string _resolveIp = "";
    private string _expectedStatus = "";
    private string _timeoutMs = "10000";
    private string _title = "";
    private string _message = "";
    private bool _popup;
    private string _seconds = "30";
    private string _timeoutSeconds = "120";
    private string _name = "";
    private string _toName = "";
    private string _keepName = "";
    private string _duplicateName = "";
    private string _onFailure = "Continue";
    private string _runWhen = "Always";

    public string Type
    {
        get => _type;
        set
        {
            _type = value;
            // 破坏性动作（删/改名/合并配置文件）默认"失败即中止"，避免"删除失败后仍继续改名"留半完成态。
            // 仅当用户未显式改过 OnFailure（仍是默认 Continue）时生效。
            if (value is "deleteProfile" or "renameProfile" or "merge" && _onFailure == "Continue")
                OnFailure = "Stop";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Type)));
        }
    }
    public string AdapterId { get => _adapterId; set { _adapterId = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdapterId))); } }
    public string Family { get => _family; set { _family = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Family))); } }
    public string Method { get => _method; set { _method = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Method))); } }
    public string Url { get => _url; set { _url = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Url))); } }
    public string ViaAdapterId { get => _viaAdapterId; set { _viaAdapterId = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ViaAdapterId))); } }
    public string ViaFamily { get => _viaFamily; set { _viaFamily = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ViaFamily))); } }
    public string SocksProxy { get => _socksProxy; set { _socksProxy = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SocksProxy))); } }
    public string ResolveHost { get => _resolveHost; set { _resolveHost = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResolveHost))); } }
    public string ResolveIp { get => _resolveIp; set { _resolveIp = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResolveIp))); } }
    public string ExpectedStatus { get => _expectedStatus; set { _expectedStatus = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpectedStatus))); } }
    public string TimeoutMs { get => _timeoutMs; set { _timeoutMs = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeoutMs))); } }
    public string Title { get => _title; set { _title = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title))); } }
    public string Message { get => _message; set { _message = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message))); } }
    public bool Popup { get => _popup; set { _popup = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Popup))); } }
    public string Seconds { get => _seconds; set { _seconds = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Seconds))); } }
    public string TimeoutSeconds { get => _timeoutSeconds; set { _timeoutSeconds = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeoutSeconds))); } }
    public string Name { get => _name; set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); } }
    public string ToName { get => _toName; set { _toName = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToName))); } }
    public string KeepName { get => _keepName; set { _keepName = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeepName))); } }
    public string DuplicateName { get => _duplicateName; set { _duplicateName = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DuplicateName))); } }
    public string OnFailure { get => _onFailure; set { _onFailure = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OnFailure))); } }
    public string RunWhen { get => _runWhen; set { _runWhen = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunWhen))); } }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ActionBase ToAction()
    {
        var action = Type switch
        {
            "renew" => (ActionBase)new RenewAdapterAction { AdapterId = AdapterId, Family = ParseFamily() },
            "restart" => new RestartAdapterAction { AdapterId = AdapterId },
            "http" => new HttpRequestAction
            {
                Method = Method,
                Url = Url.Trim(),
                ViaAdapterId = string.IsNullOrWhiteSpace(ViaAdapterId) ? null : ViaAdapterId,
                ViaAdapterFamily = ParseViaFamily(),
                SocksProxy = string.IsNullOrWhiteSpace(SocksProxy) ? null : SocksProxy.Trim(),
                ResolveHost = string.IsNullOrWhiteSpace(ResolveHost) ? null : ResolveHost.Trim(),
                ResolveIp = string.IsNullOrWhiteSpace(ResolveIp) ? null : ResolveIp.Trim(),
                ExpectedStatus = int.TryParse(ExpectedStatus.Trim(), out int st) ? st : null,
                TimeoutMs = ParseInt(TimeoutMs, 10000),
            },
            "wait" => new WaitAction { Seconds = ParseInt(Seconds, 30) },
            "waitUntil" => new WaitUntilAdapterHasAddressAction { AdapterId = AdapterId, Family = ParseFamily(), TimeoutSeconds = ParseInt(TimeoutSeconds, 120) },
            "deleteProfile" => new DeleteNetworkProfileAction { Name = Name.Trim() },
            "renameProfile" => new RenameNetworkProfileAction { FromName = Name.Trim(), ToName = ToName.Trim() },
            "merge" => new MergeNetworkProfilesAction { KeepName = KeepName.Trim(), DuplicateName = DuplicateName.Trim() },
            _ => (ActionBase)new NotifyAction { Title = Title, Message = Message, Popup = Popup },
        };
        action.OnFailure = OnFailure == "Stop" ? OnFailureMode.Stop : OnFailureMode.Continue;
        action.RunWhen = RunWhen switch
        {
            "OnlyOnPreviousFailure" => RunWhenMode.OnlyOnPreviousFailure,
            "OnlyOnPreviousSuccess" => RunWhenMode.OnlyOnPreviousSuccess,
            _ => RunWhenMode.Always,
        };
        return action;
    }

    public static ActionRowViewModel From(ActionBase a)
    {
        var vm = new ActionRowViewModel();
        // Type 赋值可能在破坏性动作时把 OnFailure 默认设为 Stop；
        // 因此 OnFailure/RunWhen 放在 switch 之后再覆盖为规则里实际保存的值。
        switch (a)
        {
            case RenewAdapterAction x:
                vm.Type = "renew"; vm.AdapterId = x.AdapterId; vm.Family = x.Family.ToString(); break;
            case RestartAdapterAction x:
                vm.Type = "restart"; vm.AdapterId = x.AdapterId; break;
            case HttpRequestAction x:
                vm.Type = "http"; vm.Method = x.Method; vm.Url = x.Url;
                vm.ViaAdapterId = x.ViaAdapterId ?? ""; vm.ViaFamily = x.ViaAdapterFamily.ToString();
                vm.SocksProxy = x.SocksProxy ?? ""; vm.ResolveHost = x.ResolveHost ?? ""; vm.ResolveIp = x.ResolveIp ?? "";
                vm.ExpectedStatus = x.ExpectedStatus?.ToString() ?? ""; vm.TimeoutMs = x.TimeoutMs.ToString(); break;
            case WaitAction x:
                vm.Type = "wait"; vm.Seconds = x.Seconds.ToString(); break;
            case WaitUntilAdapterHasAddressAction x:
                vm.Type = "waitUntil"; vm.AdapterId = x.AdapterId; vm.Family = x.Family.ToString(); vm.TimeoutSeconds = x.TimeoutSeconds.ToString(); break;
            case DeleteNetworkProfileAction x:
                vm.Type = "deleteProfile"; vm.Name = x.Name; break;
            case RenameNetworkProfileAction x:
                vm.Type = "renameProfile"; vm.Name = x.FromName; vm.ToName = x.ToName; break;
            case MergeNetworkProfilesAction x:
                vm.Type = "merge"; vm.KeepName = x.KeepName; vm.DuplicateName = x.DuplicateName; break;
            default:
                if (a is NotifyAction n) { vm.Type = "notify"; vm.Title = n.Title; vm.Message = n.Message; vm.Popup = n.Popup; }
                else vm.Type = "notify";
                break;
        }
        vm.OnFailure = a.OnFailure.ToString();
        vm.RunWhen = a.RunWhen.ToString();
        return vm;
    }

    private AddressFamilyKind ParseFamily() => Family == "IPv4" ? AddressFamilyKind.IPv4 : AddressFamilyKind.IPv6;
    private AddressFamilyKind ParseViaFamily() => ViaFamily == "IPv6" ? AddressFamilyKind.IPv6 : AddressFamilyKind.IPv4;
    private static int ParseInt(string s, int fallback) => int.TryParse(s.Trim(), out int v) ? v : fallback;
}
