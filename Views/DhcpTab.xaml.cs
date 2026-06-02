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

public partial class DhcpTab : UserControl
{
    private readonly DhcpManager _manager = new();
    private readonly ObservableCollection<NetworkAdapterInfo> _adapters = new();

    public DhcpTab()
    {
        InitializeComponent();
        AdapterGrid.ItemsSource = _adapters;
        Loaded += (_, _) => RefreshData();
    }

    private void RefreshData()
    {
        try
        {
            _adapters.Clear();
            foreach (var a in _manager.GetAdapters())
                _adapters.Add(a);
            SetStatus($"已加载 {_adapters.Count} 个网卡");
            EmptyState.Visibility = _adapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"加载网卡信息失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private List<NetworkAdapterInfo> GetSelected() =>
        AdapterGrid.SelectedItems.Cast<NetworkAdapterInfo>().ToList();

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshData();

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => AdapterGrid.SelectAll();

    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.InvertSelection(AdapterGrid, _adapters);

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

        SetStatus($"Submitting restart for {selected.Count} adapter(s)...");
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
        var successful = restartResult.successful;
        var errors = restartResult.errors;

        if (successful.Count > 0)
        {
            SetStatus($"提交重启 {successful.Count}/{selected.Count} 个网卡，5 秒后自动刷新...");
            _ = AutoRefreshAfterDelay(5000);
            var cmdPreview = string.Join("\n", successful
                .Select(a => DhcpManager.GetRestartAdapterCommandPreview(a.Name)));
            if (Window.GetWindow(this) is MainWindow mw) mw.SetCommandPreview(cmdPreview);
        }

        if (errors.Count > 0)
        {
            CopyableMessageBox.Show(
                $"成功提交重启 {successful.Count}/{selected.Count} 个网卡。\n\n失败项：\n{string.Join("\n", errors)}",
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

        SetStatus($"Submitting {proto} Release+Renew for {selected.Count} adapter(s)...");
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
        var successful = releaseResult.successful;
        var errors = releaseResult.errors;

        // 无论是否全部成功，成功的网卡都触发自动刷新并显示命令预览
        if (successful.Count > 0)
        {
            SetStatus($"{proto} Release+Renew 提交 {successful.Count}/{selected.Count} 个网卡，3 秒后自动刷新...");
            _ = AutoRefreshAfterDelay(3000);
            var cmdPreview = string.Join("\n", successful
                .Select(a => DhcpManager.GetReleaseRenewCommandPreview(a.Name, ipv6)));
            if (Window.GetWindow(this) is MainWindow mw) mw.SetCommandPreview(cmdPreview);
        }

        if (errors.Count > 0)
        {
            CopyableMessageBox.Show(
                $"成功提交 {successful.Count}/{selected.Count} 个网卡。\n\n失败项：\n{string.Join("\n", errors)}",
                "操作结果", MessageBoxImage.Warning);
        }
    }

    private async Task AutoRefreshAfterDelay(int delayMs)
    {
        await Task.Delay(delayMs);
        await Dispatcher.InvokeAsync(() =>
        {
            RefreshData();
            SetStatus("网卡信息已自动刷新");
        });
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
