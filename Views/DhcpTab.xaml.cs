using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinNetManager.Models;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class DhcpTab : UserControl, IRefreshableTab
{
    private readonly DhcpManager _manager = new();
    private readonly ObservableCollection<NetworkAdapterInfo> _adapters = new();

    public DhcpTab()
    {
        InitializeComponent();
        AdapterGrid.ItemsSource = _adapters;
        Loaded += async (_, _) => await RefreshDataAsync();
    }

    public async Task RefreshAsync() => await RefreshDataAsync();

    private async Task RefreshDataAsync()
    {
        try
        {
            var adapters = await Task.Run(() => _manager.GetAdapters());
            _adapters.Clear();
            foreach (var a in adapters)
                _adapters.Add(a);
            SetStatus($"已加载 {_adapters.Count} 块网卡");
            EmptyState.Visibility = _adapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"加载网卡信息失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private List<NetworkAdapterInfo> GetSelected() =>
        AdapterGrid.SelectedItems.Cast<NetworkAdapterInfo>().ToList();

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshDataAsync();

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => AdapterGrid.SelectAll();

    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.InvertSelection(AdapterGrid, _adapters);

    private async void BtnSetStatic_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count != 1)
        {
            CopyableMessageBox.Show("设为静态一次只能操作一块网卡，请恰好选中一项。", "未选择", MessageBoxImage.Information);
            return;
        }

        var adapter = selected[0];
        var dlg = new IpConfigEditWindow(adapter) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        var confirm = MessageBox.Show(
            $"确定将「{adapter.Name}」设为静态 IP？\n\n" +
            $"  地址：{dlg.IpAddress}/{dlg.PrefixLength}\n" +
            $"  网关：{(string.IsNullOrEmpty(dlg.Gateway) ? "（不设置）" : dlg.Gateway)}\n\n" +
            "现有 IPv4 地址与默认网关将被替换；若配置错误可能导致断网。",
            "确认设为静态",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SetStatus($"正在配置静态 IP：{adapter.Name}...");
        string ip = dlg.IpAddress;
        int prefix = dlg.PrefixLength;
        string gateway = dlg.Gateway;

        var result = await Task.Run(() => _manager.SetStaticIPv4(adapter.Name, ip, prefix, gateway));
        if (Window.GetWindow(this) is MainWindow mw)
            mw.SetCommandPreview(DhcpManager.GetSetStaticCommandPreview(adapter.Name, ip, prefix, gateway));

        if (!result.Success)
        {
            CopyableMessageBox.Show($"设置失败：{result.Message}", "错误", MessageBoxImage.Error);
            SetStatus("静态 IP 设置失败");
            return;
        }

        SetStatus($"已设为静态：{adapter.Name} → {ip}/{prefix}");
        await AutoRefreshAfterDelay(1500);
    }

    private async void BtnEnableDhcp_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count == 0)
        {
            CopyableMessageBox.Show("请先选择至少一个网卡。", "未选择", MessageBoxImage.Information);
            return;
        }

        var names = string.Join("\n", selected.Select(a => $"  • {a.Name} ({a.DhcpStatusDisplay})"));
        var confirm = MessageBox.Show(
            $"确定将以下 {selected.Count} 块网卡切换为 DHCP？\n\n{names}\n\n" +
            "静态默认网关路由会被清除，系统将通过 DHCP 重新获取地址。",
            "确认设为 DHCP",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetStatus($"正在切换 DHCP：{selected.Count} 块网卡...");
        var opResult = await Task.Run(() =>
        {
            var successful = new List<NetworkAdapterInfo>();
            var errors = new List<string>();
            foreach (var adapter in selected)
            {
                var res = _manager.EnableDhcpIPv4(adapter.Name);
                if (res.Success) successful.Add(adapter);
                else errors.Add($"{adapter.Name}: {res.Message}");
            }
            return (successful, errors);
        });

        if (opResult.successful.Count > 0)
        {
            var preview = string.Join("\n\n", opResult.successful
                .Select(a => DhcpManager.GetEnableDhcpCommandPreview(a.Name)));
            if (Window.GetWindow(this) is MainWindow mw) mw.SetCommandPreview(preview);
            SetStatus($"已切换 DHCP {opResult.successful.Count}/{selected.Count}，3 秒后刷新...");
            _ = AutoRefreshAfterDelay(3000);
        }

        if (opResult.errors.Count > 0)
        {
            CopyableMessageBox.Show(
                $"成功 {opResult.successful.Count}/{selected.Count}。\n\n失败项：\n{string.Join("\n", opResult.errors)}",
                "操作结果", MessageBoxImage.Warning);
        }
    }

    private void BtnReleaseRenew4_Click(object sender, RoutedEventArgs e) =>
        DoReleaseRenew(ipv6: false);

    private void BtnReleaseRenew6_Click(object sender, RoutedEventArgs e) =>
        DoReleaseRenew(ipv6: true);

    private async void BtnRestartAdapter_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count == 0)
        {
            CopyableMessageBox.Show("请先选择至少一个网卡。", "未选择", MessageBoxImage.Information);
            return;
        }

        var names = string.Join("\n", selected.Select(a => $"  • {a.Name}"));
        var result = MessageBox.Show(
            $"确定要重启以下 {selected.Count} 个网卡？\n\n{names}\n\n" +
            "重启网卡会导致几秒钟的网络闪断，但在后台会自动重连。即使远程连接瞬间断开，稍后也能恢复。确定要继续吗？",
            "确认重启网卡",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        SetStatus($"正在提交重启：{selected.Count} 块网卡...");
        var restartResult = await Task.Run(() =>
        {
            var successful = new List<NetworkAdapterInfo>();
            var errors = new List<string>();
            foreach (var adapter in selected)
            {
                var res = _manager.RestartAdapter(adapter.Name);
                if (res.Success) successful.Add(adapter);
                else errors.Add($"{adapter.Name}: {res.Message}");
            }
            return (successful, errors);
        });

        if (restartResult.successful.Count > 0)
        {
            SetStatus($"已提交重启 {restartResult.successful.Count}/{selected.Count}，5 秒后自动刷新...");
            _ = AutoRefreshAfterDelay(5000);
            var cmdPreview = string.Join("\n", restartResult.successful
                .Select(a => DhcpManager.GetRestartAdapterCommandPreview(a.Name)));
            if (Window.GetWindow(this) is MainWindow mw) mw.SetCommandPreview(cmdPreview);
        }

        if (restartResult.errors.Count > 0)
        {
            CopyableMessageBox.Show(
                $"成功提交重启 {restartResult.successful.Count}/{selected.Count} 个网卡。\n\n失败项：\n{string.Join("\n", restartResult.errors)}",
                "操作结果", MessageBoxImage.Warning);
        }
    }

    private async void DoReleaseRenew(bool ipv6)
    {
        var selected = GetSelected();
        if (selected.Count == 0)
        {
            CopyableMessageBox.Show("请先选择至少一个网卡。", "未选择", MessageBoxImage.Information);
            return;
        }

        string proto = ipv6 ? "IPv6" : "IPv4";
        var names = string.Join("\n", selected.Select(a => $"  • {a.Name}"));
        var result = MessageBox.Show(
            $"确定要对以下 {selected.Count} 个网卡执行 {proto} 释放+续租？\n\n{names}\n\n" +
            "命令将在后台链式执行，即使当前网络会话断开也能完成续租。",
            $"确认 {proto} Release+Renew",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        SetStatus($"正在提交 {proto} Release+Renew：{selected.Count} 块网卡...");
        var releaseResult = await Task.Run(() =>
        {
            var successful = new List<NetworkAdapterInfo>();
            var errors = new List<string>();
            foreach (var adapter in selected)
            {
                var res = _manager.ReleaseRenew(adapter.Name, ipv6);
                if (res.Success) successful.Add(adapter);
                else errors.Add($"{adapter.Name}: {res.Message}");
            }
            return (successful, errors);
        });

        if (releaseResult.successful.Count > 0)
        {
            SetStatus($"{proto} Release+Renew 提交 {releaseResult.successful.Count}/{selected.Count}，3 秒后自动刷新...");
            _ = AutoRefreshAfterDelay(3000);
            var cmdPreview = string.Join("\n", releaseResult.successful
                .Select(a => DhcpManager.GetReleaseRenewCommandPreview(a.Name, ipv6)));
            if (Window.GetWindow(this) is MainWindow mw) mw.SetCommandPreview(cmdPreview);
        }

        if (releaseResult.errors.Count > 0)
        {
            CopyableMessageBox.Show(
                $"成功提交 {releaseResult.successful.Count}/{selected.Count} 个网卡。\n\n失败项：\n{string.Join("\n", releaseResult.errors)}",
                "操作结果", MessageBoxImage.Warning);
        }
    }

    private async Task AutoRefreshAfterDelay(int delayMs)
    {
        await Task.Delay(delayMs);
        await RefreshDataAsync();
        SetStatus("网卡信息已自动刷新");
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.CopySelectedCellValue(AdapterGrid);

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var dir = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        e.Column.SortDirection = dir;
        var view = CollectionViewSource.GetDefaultView(AdapterGrid.ItemsSource);
        view.SortDescriptions.Clear();
        string prop = (e.Column as DataGridBoundColumn)?.Binding is Binding b ? b.Path.Path : "";
        view.SortDescriptions.Add(new SortDescription(prop, dir));
        if (view is ListCollectionView lcv)
            lcv.CustomSort = new NaturalSortByProperty(prop, dir);
    }

    private void SetStatus(string msg)
    {
        if (Window.GetWindow(this) is MainWindow mw) mw.SetStatus(msg);
    }
}
