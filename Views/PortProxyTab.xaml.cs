using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinNetManager.Models;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class PortProxyTab : UserControl
{
    private readonly PortProxyManager _manager = new();
    private List<PortProxyRule> _allRules = new();
    private ICollectionView? _ruleView;

    public PortProxyTab()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshDataAsync();
    }

    private async Task RefreshDataAsync()
    {
        try
        {
            var (rules, svcRunning) = await Task.Run(() =>
            {
                var r = _manager.GetRules();
                bool s = _manager.IsServiceRunning();
                return (r, s);
            });
            _allRules = rules;
            _ruleView = CollectionViewSource.GetDefaultView(_allRules);
            _ruleView.Filter = RuleFilter;
            ProxyGrid.ItemsSource = _ruleView;
            UpdateCount();
            TbkServiceWarning.Visibility = svcRunning ? Visibility.Collapsed : Visibility.Visible;
            EmptyState.Visibility = _allRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"加载端口转发规则失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private void RefreshData()
    {
        try
        {
            _allRules = _manager.GetRules();
            _ruleView = CollectionViewSource.GetDefaultView(_allRules);
            _ruleView.Filter = RuleFilter;
            ProxyGrid.ItemsSource = _ruleView;
            UpdateCount();
            CheckServiceStatus();
            EmptyState.Visibility = _allRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"加载端口转发规则失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private void CheckServiceStatus()
    {
        bool running = _manager.IsServiceRunning();
        TbkServiceWarning.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool RuleFilter(object obj)
    {
        if (obj is not PortProxyRule rule) return false;

        var selectedDirection = (CmbDirection.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (!string.IsNullOrEmpty(selectedDirection) && rule.Direction != selectedDirection)
            return false;

        string filter = TxtFilter.Text?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(filter)) return true;

        return (rule.ListenAddress?.ToLowerInvariant().Contains(filter) == true)
            || (rule.ListenPort?.ToLowerInvariant().Contains(filter) == true)
            || (rule.ConnectAddress?.ToLowerInvariant().Contains(filter) == true)
            || (rule.ConnectPort?.ToLowerInvariant().Contains(filter) == true)
            || (rule.Direction?.ToLowerInvariant().Contains(filter) == true);
    }

    private void UpdateCount()
    {
        int total = _allRules.Count;
        int visible = _ruleView?.Cast<object>().Count() ?? 0;
        TbkCount.Text = visible == total
            ? $"共 {total} 条规则"
            : $"显示 {visible} / 共 {total} 条规则";
        SetStatus(TbkCount.Text);

        EmptyState.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<PortProxyRule> GetSelected() =>
        ProxyGrid.SelectedItems.Cast<PortProxyRule>().ToList();

    // --- 按钮事件 ---

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshDataAsync();

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => ProxyGrid.SelectAll();

    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.InvertSelection(ProxyGrid, _allRules);

    private async void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        var editWindow = new PortProxyEditWindow(null);
        editWindow.Owner = Window.GetWindow(this);

        if (editWindow.ShowDialog() == true)
        {
            var rule = editWindow.Rule;
            var (result, fwRes) = await Task.Run(() =>
            {
                var r = _manager.AddRule(rule);
                var f = r.Success ? _manager.AddFirewallRule(rule) : new PortProxyResult { Success = false };
                return (r, f);
            });
            if (result.Success)
            {
                string fwMsg = fwRes.Success ? "（防火墙规则已添加）" : $"（防火墙规则添加失败：{fwRes.Message}）";
                SetStatus($"已添加端口转发：{rule.ListenAddress}:{rule.ListenPort} → {rule.ConnectAddress}:{rule.ConnectPort} {fwMsg}");
                string fwName = PortProxyManager.GetFirewallRuleName(rule);
                var cmd = $"netsh interface portproxy add {rule.Direction} listenaddress=\"{rule.ListenAddress}\" listenport=\"{rule.ListenPort}\" connectaddress=\"{rule.ConnectAddress}\" connectport=\"{rule.ConnectPort}\" protocol=\"{rule.Protocol}\"";
                if (fwRes.Success)
                    cmd += $"\nnetsh advfirewall firewall add rule name=\"{fwName}\" dir=in action=allow protocol={rule.Protocol} localport={rule.ListenPort}";
                if (Window.GetWindow(this) is MainWindow mw) mw.SetCommandPreview(cmd);
                await RefreshDataAsync();
            }
            else
            {
                CopyableMessageBox.Show($"添加失败：{result.Message}", "错误", MessageBoxImage.Error);
            }
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
        var editWindow = new PortProxyEditWindow(original);
        editWindow.Owner = Window.GetWindow(this);

        if (editWindow.ShowDialog() != true) return;

        var edited = editWindow.Rule;

        var cmdPreview = new StringBuilder();
        if (!original.EqualsWithKeys(edited))
        {
            cmdPreview.AppendLine($"netsh interface portproxy delete {original.Direction} listenaddress=\"{original.ListenAddress}\" listenport=\"{original.ListenPort}\"");
            cmdPreview.AppendLine($"netsh advfirewall firewall delete rule name=\"{PortProxyManager.GetFirewallRuleName(original)}\"");

            var (delRes, delFwRes) = await Task.Run(() =>
            {
                var d = _manager.DeleteRule(original);
                var f = _manager.DeleteFirewallRule(original);
                return (d, f);
            });
            if (!delRes.Success)
            {
                CopyableMessageBox.Show($"删除原规则失败：{delRes.Message}", "错误", MessageBoxImage.Error);
                return;
            }
            if (!delFwRes.Success && delFwRes.Message.IndexOf("找不到", StringComparison.OrdinalIgnoreCase) < 0)
            {
                CopyableMessageBox.Show($"原端口转发已删除，但旧防火墙规则删除失败：\n{delFwRes.Message}",
                    "防火墙警告", MessageBoxImage.Warning);
            }

            cmdPreview.AppendLine($"netsh interface portproxy add {edited.Direction} listenaddress=\"{edited.ListenAddress}\" listenport=\"{edited.ListenPort}\" connectaddress=\"{edited.ConnectAddress}\" connectport=\"{edited.ConnectPort}\" protocol=\"{edited.Protocol}\"");
            cmdPreview.AppendLine($"netsh advfirewall firewall add rule name=\"{PortProxyManager.GetFirewallRuleName(edited)}\" dir=in action=allow protocol={edited.Protocol} localport={edited.ListenPort}");

            var (addRes, addFwRes) = await Task.Run(() =>
            {
                var a = _manager.AddRule(edited);
                var f = a.Success ? _manager.AddFirewallRule(edited) : new PortProxyResult { Success = false };
                return (a, f);
            });
            if (addRes.Success)
            {
                string fwMsg = addFwRes.Success ? "（防火墙规则已更新）" : $"（防火墙规则添加失败：{addFwRes.Message}）";
                SetStatus($"已修改端口转发：{edited.ListenAddress}:{edited.ListenPort} → {edited.ConnectAddress}:{edited.ConnectPort} {fwMsg}");
            }
            else
            {
                CopyableMessageBox.Show($"添加新规则失败：{addRes.Message}", "错误", MessageBoxImage.Error);
                var (restoreRes, restoreFw) = await Task.Run(() =>
                {
                    var r = _manager.AddRule(original);
                    var f = _manager.AddFirewallRule(original);
                    return (r, f);
                });
                if (!restoreRes.Success || !restoreFw.Success)
                {
                    string details = $"旧规则恢复：{(restoreRes.Success ? "成功" : "失败")}，防火墙：{(restoreFw.Success ? "成功" : "失败")}";
                    CopyableMessageBox.Show($"旧规则可能已丢失。{details}", "严重警告", MessageBoxImage.Warning);
                }
            }
        }
        else
        {
            cmdPreview.AppendLine($"netsh interface portproxy set {edited.Direction} listenaddress=\"{edited.ListenAddress}\" listenport=\"{edited.ListenPort}\" connectaddress=\"{edited.ConnectAddress}\" connectport=\"{edited.ConnectPort}\" protocol=\"{edited.Protocol}\"");
            cmdPreview.AppendLine($"netsh advfirewall firewall delete rule name=\"{PortProxyManager.GetFirewallRuleName(original)}\"");
            cmdPreview.AppendLine($"netsh advfirewall firewall add rule name=\"{PortProxyManager.GetFirewallRuleName(edited)}\" dir=in action=allow protocol={edited.Protocol} localport={edited.ListenPort}");

            var (setRes, fwRes) = await Task.Run(() =>
            {
                var s = _manager.SetRule(edited);
                var f = s.Success ? _manager.UpdateFirewallRule(original, edited) : new PortProxyResult { Success = false };
                return (s, f);
            });
            if (setRes.Success)
            {
                string fwMsg = fwRes.Success ? "（防火墙规则已更新）" : $"（防火墙规则更新失败：{fwRes.Message}）";
                SetStatus($"已修改端口转发：{edited.ListenAddress}:{edited.ListenPort} → {edited.ConnectAddress}:{edited.ConnectPort} {fwMsg}");
            }
            else
            {
                CopyableMessageBox.Show($"修改失败：{setRes.Message}", "错误", MessageBoxImage.Error);
            }
        }

        if (Window.GetWindow(this) is MainWindow mw2) mw2.SetCommandPreview(cmdPreview.ToString().Trim());
        await RefreshDataAsync();
    }

    private async void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count == 0)
        {
            CopyableMessageBox.Show("请先选择要删除的规则。", "未选择", MessageBoxImage.Information);
            return;
        }

        var names = string.Join("\n", selected.Select(r => $"  • {r.Direction}: {r.ListenAddress}:{r.ListenPort} → {r.ConnectAddress}:{r.ConnectPort}"));
        if (MessageBox.Show($"确定要删除以下 {selected.Count} 条端口转发规则？\n\n{names}",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var results = await Task.Run(() =>
        {
            var list = new List<(PortProxyRule rule, PortProxyResult delRes)>();
            foreach (var rule in selected)
            {
                var res = _manager.DeleteRule(rule);
                if (res.Success) _manager.DeleteFirewallRule(rule);
                list.Add((rule, res));
            }
            return list;
        });

        int ok = results.Count(r => r.delRes.Success);
        var errors = results.Where(r => !r.delRes.Success)
            .Select(r => $"{r.rule.ListenAddress}:{r.rule.ListenPort} - {r.delRes.Message}").ToList();

        if (errors.Count > 0)
        {
            CopyableMessageBox.Show($"成功删除 {ok}/{selected.Count} 条。\n\n失败项：\n{string.Join("\n", errors)}",
                "操作结果", MessageBoxImage.Warning);
        }
        else
        {
            SetStatus($"已删除 {ok}/{selected.Count} 条端口转发规则（含防火墙规则）");
            var cmd = string.Join("\n", selected.Select(r =>
            {
                string fwName = PortProxyManager.GetFirewallRuleName(r);
                return $"netsh interface portproxy delete {r.Direction} listenaddress=\"{r.ListenAddress}\" listenport=\"{r.ListenPort}\"\n" +
                       $"netsh advfirewall firewall delete rule name=\"{fwName}\"";
            }));
            if (Window.GetWindow(this) is MainWindow mw) mw.SetCommandPreview(cmd);
        }

        await RefreshDataAsync();
    }

    // --- 筛选 ---

    private void CmbDirection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _ruleView?.Refresh();
        UpdateCount();
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _ruleView?.Refresh();
        UpdateCount();
    }

    private void BtnClearFilter_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        TxtFilter.Clear();
        CmbDirection.SelectedIndex = 0;
        _ruleView?.Refresh();
        UpdateCount();
    }

    // --- 右键菜单 ---

    private void MenuCopy_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.CopySelectedCellValue(ProxyGrid);

    // --- 排序 ---

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var dir = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        e.Column.SortDirection = dir;
        var view = CollectionViewSource.GetDefaultView(ProxyGrid.ItemsSource);
        view.SortDescriptions.Clear();
        string prop = (e.Column as DataGridBoundColumn)?.Binding is Binding b ? b.Path.Path : "";
        view.SortDescriptions.Add(new SortDescription(prop, dir));
        if (view is ListCollectionView lcv)
            lcv.CustomSort = new NaturalSortByProperty(prop, dir);
    }

    // --- 导入导出 ---

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出端口转发配置",
            FileName = $"WinNetManager_Proxy_{DateTime.Now:yyyyMMdd}",
            DefaultExt = ".json",
            Filter = "JSON 文件 (*.json)|*.json",
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var currentRules = _manager.GetRules();
            var config = new WinNetConfig
            {
                PortProxyRules = ConfigExportService.ToProxyConfigs(currentRules),
            };
            ConfigExportService.Export(dlg.FileName, config);

            var msg = $"已导出 {currentRules.Count} 条端口转发规则到：\n{dlg.FileName}";
            CopyableMessageBox.Show(msg, "导出完成", MessageBoxImage.Information);
            SetStatus(msg);
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入端口转发配置",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var config = ConfigExportService.Import(dlg.FileName);
            var importedRules = ConfigExportService.ToProxyRules(config.PortProxyRules);

            if (importedRules.Count == 0)
            {
                CopyableMessageBox.Show("配置文件中未找到端口转发规则数据。", "提示", MessageBoxImage.Information);
                return;
            }

            RefreshData();

            var toAdd = new List<PortProxyRule>();
            var toOverwrite = new List<(PortProxyRule OldRule, PortProxyRule NewRule)>();
            int skipped = 0;

            foreach (var imported in importedRules)
            {
                var exactMatch = _allRules.FirstOrDefault(r =>
                    r.Direction == imported.Direction &&
                    r.ListenAddress == imported.ListenAddress &&
                    r.ListenPort == imported.ListenPort &&
                    r.ConnectAddress == imported.ConnectAddress &&
                    r.ConnectPort == imported.ConnectPort);

                if (exactMatch != null)
                {
                    skipped++;
                    continue;
                }

                var keyMatch = _allRules.FirstOrDefault(r => r.EqualsWithKeys(imported));
                if (keyMatch != null)
                {
                    toOverwrite.Add((keyMatch, imported));
                }
                else
                {
                    toAdd.Add(imported);
                }
            }

            // 无冲突时直接添加
            if (toOverwrite.Count == 0)
            {
                ExecuteImport(toAdd, new List<(PortProxyRule, PortProxyRule)>(), skipped);
                return;
            }

            // 有冲突，显示确认对话框
            var sb = new StringBuilder();
            sb.AppendLine($"即将导入 {importedRules.Count} 条端口转发规则：\n");
            sb.AppendLine($"新增：{toAdd.Count} 条");
            sb.AppendLine($"冲突（待覆盖）：{toOverwrite.Count} 条");
            sb.AppendLine($"跳过（已存在）：{skipped} 条\n");
            sb.AppendLine("冲突项详情：");
            foreach (var (old, nw) in toOverwrite)
            {
                sb.AppendLine($"  • {old.Direction}: {old.ListenAddress}:{old.ListenPort}");
                sb.AppendLine($"    原目标 {old.ConnectAddress}:{old.ConnectPort} → 新目标 {nw.ConnectAddress}:{nw.ConnectPort}");
            }
            sb.AppendLine("\n「是」= 覆盖冲突项并新增\n「否」= 仅新增，跳过冲突\n「取消」= 放弃导入");

            var result = MessageBox.Show(sb.ToString(), "导入确认", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) return;

            bool overwrite = result == MessageBoxResult.Yes;
            ExecuteImport(toAdd, overwrite ? toOverwrite : new List<(PortProxyRule, PortProxyRule)>(), skipped);
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"导入失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private void ExecuteImport(List<PortProxyRule> toAdd, List<(PortProxyRule OldRule, PortProxyRule NewRule)> toOverwrite, int skipped)
    {
        int ok = 0;
        var errors = new List<string>();
        var warnings = new List<string>();

        foreach (var rule in toAdd)
        {
            if (!PortProxyManager.ValidateRule(rule, out string valErr))
            {
                errors.Add($"新增 {rule.ListenAddress}:{rule.ListenPort} → {rule.ConnectAddress}:{rule.ConnectPort}：{valErr}");
                continue;
            }
            var res = _manager.AddRule(rule);
            if (res.Success)
            {
                ok++;
                var fwRes = _manager.AddFirewallRule(rule);
                if (!fwRes.Success)
                    warnings.Add($"端口转发已添加，但防火墙规则创建失败：{rule.ListenAddress}:{rule.ListenPort} — {fwRes.Message}");
            }
            else errors.Add($"新增 {rule.ListenAddress}:{rule.ListenPort} → {rule.ConnectAddress}:{rule.ConnectPort}：{res.Message}");
        }

        foreach (var (old, nw) in toOverwrite)
        {
            if (!PortProxyManager.ValidateRule(nw, out string valErr))
            {
                errors.Add($"覆盖 {nw.ListenAddress}:{nw.ListenPort} → {nw.ConnectAddress}:{nw.ConnectPort}：{valErr}");
                continue;
            }
            var delRes = _manager.DeleteRule(old);
            if (!delRes.Success)
            {
                errors.Add($"删除旧规则 {old.ListenAddress}:{old.ListenPort}：{delRes.Message}");
                continue;
            }
            var delFwRes = _manager.DeleteFirewallRule(old);
            if (!delFwRes.Success && delFwRes.Message.IndexOf("找不到", StringComparison.OrdinalIgnoreCase) < 0)
                warnings.Add($"旧防火墙规则删除失败：{old.ListenAddress}:{old.ListenPort} — {delFwRes.Message}");

            var addRes = _manager.AddRule(nw);
            if (addRes.Success)
            {
                ok++;
                var fwRes = _manager.AddFirewallRule(nw);
                if (!fwRes.Success)
                    warnings.Add($"端口转发已更新，但防火墙规则创建失败：{nw.ListenAddress}:{nw.ListenPort} — {fwRes.Message}");
            }
            else
            {
                errors.Add($"添加新规则 {nw.ListenAddress}:{nw.ListenPort} → {nw.ConnectAddress}:{nw.ConnectPort}：{addRes.Message}");
                // 尝试回滚旧规则
                var rbRes = _manager.AddRule(old);
                var rbFw = _manager.AddFirewallRule(old);
                if (!rbRes.Success || !rbFw.Success)
                    warnings.Add($"新规则添加失败，旧规则回滚：端口转发{(rbRes.Success ? "成功" : "失败")}，防火墙{(rbFw.Success ? "成功" : "失败")}");
            }
        }

        RefreshData();

        var msg = $"导入完成：成功 {ok} 条，跳过 {skipped} 条。";
        if (warnings.Count > 0)
        {
            msg += $"\n\n警告 {warnings.Count} 条：\n{string.Join("\n", warnings)}";
        }
        if (errors.Count > 0)
        {
            msg += $"\n\n失败 {errors.Count} 条：\n{string.Join("\n", errors)}";
            CopyableMessageBox.Show(msg, "导入结果", MessageBoxImage.Warning);
        }
        else
        {
            CopyableMessageBox.Show(msg, warnings.Count > 0 ? "导入完成（有警告）" : "导入完成",
                warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        SetStatus(msg);
    }

    private void SetStatus(string msg)
    {
        if (Window.GetWindow(this) is MainWindow mw) mw.SetStatus(msg);
    }
}
