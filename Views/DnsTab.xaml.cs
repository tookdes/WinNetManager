using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinNetManager.Models;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class DnsTab : UserControl
{
    private readonly DnsNrptManager _manager = new();
    private readonly ObservableCollection<NrptRule> _rules = new();
    private List<InterfaceDnsInfo> _allDns = new();
    private ICollectionView? _dnsView;
    private ICollectionView? _nrptView;

    public DnsTab()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshData();
    }

    private void RefreshData()
    {
        try
        {
            _rules.Clear();
            foreach (var r in _manager.GetRules())
                _rules.Add(r);
            _nrptView = CollectionViewSource.GetDefaultView(_rules);
            _nrptView.Filter = NrptFilter;
            NrptGrid.ItemsSource = _nrptView;

            _allDns = _manager.GetInterfaceDnsServers();
            _dnsView = CollectionViewSource.GetDefaultView(_allDns);
            _dnsView.Filter = DnsFilter;
            InterfaceDnsGrid.ItemsSource = _dnsView;

            UpdateCount();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载配置失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool DnsFilter(object obj)
    {
        if (obj is not InterfaceDnsInfo item) return false;
        var family = (CmbAddressFamily.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (!string.IsNullOrEmpty(family) && item.AddressFamily != family) return false;

        string filter = TxtFilter.Text?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(filter)) return true;

        return (item.InterfaceAlias?.ToLowerInvariant().Contains(filter) == true)
            || (item.ServerAddresses?.ToLowerInvariant().Contains(filter) == true);
    }

    private bool NrptFilter(object obj)
    {
        if (obj is not NrptRule item) return false;
        string filter = TxtFilter.Text?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(filter)) return true;

        return (item.Namespace?.ToLowerInvariant().Contains(filter) == true)
            || (item.NameServers?.ToLowerInvariant().Contains(filter) == true)
            || (item.Comment?.ToLowerInvariant().Contains(filter) == true);
    }

    private void UpdateCount()
    {
        int totalNrpt = _rules.Count;
        int visibleNrpt = _nrptView?.Cast<object>().Count() ?? 0;
        TbkNrptCount.Text = visibleNrpt == totalNrpt ? $"共 {totalNrpt} 条" : $"显示 {visibleNrpt} / 共 {totalNrpt} 条";
        EmptyState.Visibility = visibleNrpt == 0 ? Visibility.Visible : Visibility.Collapsed;

        int totalDns = _allDns.Count;
        int visibleDns = _dnsView?.Cast<object>().Count() ?? 0;
        TbkDnsCount.Text = visibleDns == totalDns ? $"共 {totalDns} 条" : $"显示 {visibleDns} / 共 {totalDns} 条";
    }

    private List<InterfaceDnsInfo> GetSelectedDns() =>
        InterfaceDnsGrid.SelectedItems.Cast<InterfaceDnsInfo>().ToList();

    private List<NrptRule> GetSelected() =>
        NrptGrid.SelectedItems.Cast<NrptRule>().ToList();

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshData();
    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => NrptGrid.SelectAll();
    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.InvertSelection(NrptGrid, _rules);

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var ns = NetworkProfileTab.PromptInput("新建 NRPT 规则",
            "域名后缀（如 *.corp.local 或 corp.local）：", "*.corp.local");
        if (string.IsNullOrEmpty(ns)) return;

        var servers = NetworkProfileTab.PromptInput("新建 NRPT 规则",
            "DNS 服务器地址（多个用逗号分隔）：", "10.0.0.1");
        if (string.IsNullOrEmpty(servers)) return;

        var comment = NetworkProfileTab.PromptInput("新建 NRPT 规则",
            "备注（可选）：", "");

        var res = _manager.AddRule(ns, servers, comment);
        if (res.Success)
        {
            SetStatus($"已添加 NRPT 规则：{ns} -> {servers}");
            RefreshData();
        }
        else
        {
            MessageBox.Show($"添加失败：{res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择要删除的规则。", "未选择", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var names = string.Join("\n", selected.Select(r => $"  • {r.Namespace} -> {r.NameServers}"));
        if (MessageBox.Show($"确定要删除以下 {selected.Count} 条 NRPT 规则？\n\n{names}",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        int ok = 0;
        var errors = new List<string>();
        foreach (var rule in selected)
        {
            var res = _manager.DeleteRule(rule.Name, rule.GpoName);
            if (res.Success) ok++;
            else errors.Add($"{rule.Namespace}: {res.Message}");
        }

        if (errors.Count > 0)
        {
            MessageBox.Show($"成功删除 {ok}/{selected.Count} 条。\n\n失败项：\n{string.Join("\n", errors)}",
                "操作结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            SetStatus($"已删除 {ok}/{selected.Count} 条 NRPT 规则");
        }

        RefreshData();
    }

    private void BtnResolve_Click(object sender, RoutedEventArgs e)
    {
        string domain = TxtResolveDomain.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(domain))
        {
            MessageBox.Show("请输入要解析的域名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TxtResolveResult.Text = $"正在解析 {domain}...";

        try
        {
            var res = _manager.ResolveDomain(domain);
            if (res.Success)
            {
                TxtResolveResult.Text = res.Records.Count > 0
                    ? string.Join("\n", res.Records)
                    : "未解析到记录。";
                SetStatus($"{domain}：{res.Message}");
            }
            else
            {
                TxtResolveResult.Text = $"解析失败：{res.Message}";
                SetStatus($"解析 {domain} 失败");
            }
        }
        catch (Exception ex)
        {
            TxtResolveResult.Text = $"错误：{ex.Message}";
        }
    }

    private void BtnFlushDns_Click(object sender, RoutedEventArgs e)
    {
        var res = _manager.FlushCache();
        if (res.Success)
        {
            TbkFlushResult.Text = "DNS 缓存已刷新。";
            TbkFlushResult.Foreground = System.Windows.Media.Brushes.Green;
            SetStatus("DNS 缓存已刷新");
        }
        else
        {
            TbkFlushResult.Text = $"刷新失败：{res.Message}";
            TbkFlushResult.Foreground = System.Windows.Media.Brushes.Red;
        }
    }

    private void MenuCopyDns_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.CopySelectedCellValue(InterfaceDnsGrid);

    private void MenuCopyNrpt_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.CopySelectedCellValue(NrptGrid);

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var dir = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        e.Column.SortDirection = dir;
        var grid = sender as DataGrid;
        if (grid == null) return;
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        view.SortDescriptions.Clear();
        string prop = (e.Column as DataGridBoundColumn)?.Binding is Binding b ? b.Path.Path : "";
        view.SortDescriptions.Add(new SortDescription(prop, dir));
        if (view is ListCollectionView lcv)
            lcv.CustomSort = new NaturalSortByProperty(prop, dir);
    }

    private void CmbAddressFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _dnsView?.Refresh();
        UpdateCount();
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _dnsView?.Refresh();
        _nrptView?.Refresh();
        UpdateCount();
    }

    private void BtnClearFilter_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        TxtFilter.Clear();
        CmbAddressFamily.SelectedIndex = 0;
        _dnsView?.Refresh();
        _nrptView?.Refresh();
        UpdateCount();
    }

    private void BtnEditDns_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedDns();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择至少一个网卡。", "未选择", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var defaultVal = selected.First().ServerAddresses;
        string? input = NetworkProfileTab.PromptInput(
            "修改 DNS 服务器",
            $"为 {selected.Count} 个网卡设置新的 DNS 服务器地址（多个用逗号分隔）：\n" +
            string.Join("\n", selected.Select(s => $"  • {s.InterfaceAlias} ({s.AddressFamily})")),
            defaultVal,
            Window.GetWindow(this));

        if (input == null) return;

        var errors = new List<string>();
        foreach (var item in selected)
        {
            var result = _manager.SetInterfaceDns(item.InterfaceAlias, item.AddressFamily, input);
            if (!result.Success)
            {
                errors.Add($"{item.InterfaceAlias} ({item.AddressFamily}): {result.Message}");
            }
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(
                $"成功 {selected.Count - errors.Count}/{selected.Count} 个。\n\n失败项：\n{string.Join("\n", errors)}",
                "操作结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshData();
        SetStatus($"DNS 服务器已更新，{selected.Count - errors.Count}/{selected.Count} 成功");
    }

    private void BtnAutoDns_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedDns();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择至少一个网卡。", "未选择", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var names = string.Join("\n", selected.Select(s => $"  • {s.InterfaceAlias} ({s.AddressFamily})"));
        if (MessageBox.Show($"确定要为以下 {selected.Count} 个网卡恢复为自动获取 DNS？\n\n{names}",
            "确认自动获取", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var errors = new List<string>();
        foreach (var item in selected)
        {
            var result = _manager.SetInterfaceDns(item.InterfaceAlias, item.AddressFamily, "");
            if (!result.Success)
            {
                errors.Add($"{item.InterfaceAlias} ({item.AddressFamily}): {result.Message}");
            }
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(
                $"成功 {selected.Count - errors.Count}/{selected.Count} 个。\n\n失败项：\n{string.Join("\n", errors)}",
                "操作结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshData();
        SetStatus($"DNS 已设为自动获取，{selected.Count - errors.Count}/{selected.Count} 成功");
    }

    private void MenuCopyAllResolve_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtResolveResult.Text))
            Clipboard.SetText(TxtResolveResult.Text);
    }

    private void MenuCopySelectedResolve_Click(object sender, RoutedEventArgs e)
    {
        string text = !string.IsNullOrEmpty(TxtResolveResult.SelectedText)
            ? TxtResolveResult.SelectedText
            : TxtResolveResult.Text;
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    private void SetStatus(string msg)
    {
        if (Window.GetWindow(this) is MainWindow mw) mw.SetStatus(msg);
    }
}
