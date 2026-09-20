using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinNetManager.Models;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class FirewallTab : UserControl, IRefreshableTab
{
    private readonly FirewallManager _manager = new();
    private readonly RoutingManager _routing = new();
    private List<FirewallRuleInfo> _allRules = new();
    private ICollectionView? _view;
    private bool _loading;

    public FirewallTab()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadDataAsync();
    }

    public async Task RefreshAsync() => await LoadDataAsync();

    private async Task LoadDataAsync()
    {
        try
        {
            _loading = true;
            bool ownOnly = ChkOwnOnly.IsChecked != false;
            var rules = await Task.Run(() => _manager.GetInboundRules(ownOnly));
            _allRules = rules;
            _view = CollectionViewSource.GetDefaultView(_allRules);
            _view.Filter = RuleFilter;
            RuleGrid.ItemsSource = _view;
            UpdateCount();
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show("加载防火墙规则失败：" + ex.Message, "错误", MessageBoxImage.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private bool RuleFilter(object obj)
    {
        if (obj is not FirewallRuleInfo rule) return false;
        string filter = TxtFilter.Text?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(filter)) return true;
        return (rule.DisplayName?.ToLowerInvariant().Contains(filter) == true)
            || (rule.LocalPort?.ToLowerInvariant().Contains(filter) == true)
            || (rule.InterfaceAlias?.ToLowerInvariant().Contains(filter) == true)
            || (rule.Protocol?.ToLowerInvariant().Contains(filter) == true)
            || (rule.LocalAddress?.ToLowerInvariant().Contains(filter) == true)
            || (rule.Profile?.ToLowerInvariant().Contains(filter) == true);
    }

    private void UpdateCount()
    {
        int total = _allRules?.Count ?? 0;
        int visible = _view?.Cast<object>().Count() ?? 0;
        TbkCount.Text = visible == total ? $"共 {total}  条规则" : $"显示 {visible} / 共 {total}  条规则";
        EmptyState.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(TbkCount.Text);
    }

    private List<FirewallRuleInfo> GetSelected() =>
        RuleGrid.SelectedItems.Cast<FirewallRuleInfo>().ToList();

    private List<string> GetInterfaceAliases()
    {
        try
        {
            return _routing.GetInterfaces()
                .Select(i => i.InterfaceAlias)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadDataAsync();
    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => RuleGrid.SelectAll();
    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.InvertSelection(RuleGrid, _allRules);

    private async void ChkOwnOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loading) return;
        await LoadDataAsync();
    }

    private async void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new FirewallRuleEditWindow(null, GetInterfaceAliases()) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        var rule = dlg.Rule;
        var result = await Task.Run(() => _manager.AddRule(rule));
        if (result.Success)
        {
            SetStatus("已添加防火墙规则：" + rule.DisplayName);
            Preview(FirewallManager.GetAddCommandPreview(rule));
            await LoadDataAsync();
        }
        else
        {
            CopyableMessageBox.Show("添加失败：" + result.Message, "错误", MessageBoxImage.Error);
        }
    }

    private async void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count != 1)
        {
            CopyableMessageBox.Show("请选择一条规则进行修改。", "未选择", MessageBoxImage.Information);
            return;
        }

        var original = selected[0];
        var dlg = new FirewallRuleEditWindow(original, GetInterfaceAliases()) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        var updated = dlg.Rule;
        updated.Name = original.Name;
        var result = await Task.Run(() => _manager.ReplaceRule(original, updated));
        if (result.Success)
        {
            SetStatus("已修改防火墙规则：" + updated.DisplayName);
            var preview = FirewallManager.GetAddCommandPreview(updated) + "\n" + FirewallManager.GetDeleteCommandPreview(original);
            Preview(preview);
            await LoadDataAsync();
        }
        else
        {
            CopyableMessageBox.Show("修改失败：" + result.Message, "错误", MessageBoxImage.Error);
        }
    }

    private async void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count == 0)
        {
            CopyableMessageBox.Show("请先选择要删除的规则。", "未选择", MessageBoxImage.Information);
            return;
        }

        var names = string.Join("\n", selected.Select(r => "  - " + r.DisplayName));
        if (MessageBox.Show($"确定删除以下 {selected.Count}  条防火墙规则？\n\n{names}", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var results = await Task.Run(() =>
        {
            var list = new List<(FirewallRuleInfo rule, FirewallCommandResult result)>();
            foreach (var rule in selected)
                list.Add((rule, _manager.DeleteRule(rule)));
            return list;
        });

        var errors = results.Where(r => !r.result.Success).Select(r => r.rule.DisplayName + ": " + r.result.Message).ToList();
        var preview = new StringBuilder();
        foreach (var r in results.Where(x => x.result.Success))
            preview.AppendLine(FirewallManager.GetDeleteCommandPreview(r.rule));
        if (preview.Length > 0) Preview(preview.ToString().Trim());

        if (errors.Count > 0)
            CopyableMessageBox.Show($"成功 {selected.Count - errors.Count}/{selected.Count}\n\n{string.Join("\n", errors)}", "操作结果", MessageBoxImage.Warning);

        await LoadDataAsync();
    }

    private async void BtnEnable_Click(object sender, RoutedEventArgs e) => await SetEnabledAsync(true);
    private async void BtnDisable_Click(object sender, RoutedEventArgs e) => await SetEnabledAsync(false);

    private async Task SetEnabledAsync(bool enabled)
    {
        var selected = GetSelected();
        if (selected.Count == 0)
        {
            CopyableMessageBox.Show("请先选择至少一条规则。", "未选择", MessageBoxImage.Information);
            return;
        }

        var results = await Task.Run(() =>
        {
            var list = new List<(FirewallRuleInfo rule, FirewallCommandResult result)>();
            foreach (var rule in selected)
                list.Add((rule, _manager.SetEnabled(rule, enabled)));
            return list;
        });

        var errors = results.Where(r => !r.result.Success).Select(r => r.rule.DisplayName + ": " + r.result.Message).ToList();
        if (errors.Count > 0)
            CopyableMessageBox.Show(string.Join("\n", errors), "操作结果", MessageBoxImage.Warning);

        SetStatus((enabled ? "已启用 " : "已禁用 ") + $"{selected.Count - errors.Count}/{selected.Count}");
        await LoadDataAsync();
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _view?.Refresh();
        UpdateCount();
    }

    private void BtnClearFilter_Click(object sender, RoutedEventArgs e)
    {
        TxtFilter.Clear();
        _view?.Refresh();
        UpdateCount();
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.CopySelectedCellValue(RuleGrid);

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var dir = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        e.Column.SortDirection = dir;
        var view = CollectionViewSource.GetDefaultView(RuleGrid.ItemsSource);
        view.SortDescriptions.Clear();
        string prop = (e.Column as DataGridBoundColumn)?.Binding is Binding b ? b.Path.Path : "";
        view.SortDescriptions.Add(new SortDescription(prop, dir));
        if (view is ListCollectionView lcv)
            lcv.CustomSort = new NaturalSortByProperty(prop, dir);
    }

    private void Preview(string command)
    {
        if (Window.GetWindow(this) is MainWindow mw)
            mw.SetCommandPreview(command);
    }

    private void SetStatus(string msg)
    {
        if (Window.GetWindow(this) is MainWindow mw) mw.SetStatus(msg);
    }
}
