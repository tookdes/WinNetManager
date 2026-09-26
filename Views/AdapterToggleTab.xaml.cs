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

public partial class AdapterToggleTab : UserControl, IRefreshableTab
{
    private readonly AdapterToggleManager _manager = new();
    private readonly ObservableCollection<AdapterToggleInfo> _adapters = new();

    public AdapterToggleTab()
    {
        InitializeComponent();
        ToggleGrid.ItemsSource = _adapters;
        Loaded += async (_, _) => await RefreshDataAsync();
    }

    public async Task RefreshAsync() => await RefreshDataAsync();

    private async Task RefreshDataAsync()
    {
        try
        {
            var (adapters, globalPrivacy, globalTemp) = await Task.Run(() =>
            {
                var a = _manager.GetAdapterToggles();
                var gp = _manager.GetGlobalIPv6Privacy();
                var gt = _manager.GetGlobalUseTemporaryAddresses();
                return (a, gp, gt);
            });

            _adapters.Clear();
            foreach (var a in adapters)
                _adapters.Add(a);

            // Update global privacy display
            string gpDisplay = FormatGlobalPrivacy(globalPrivacy.Message, globalTemp.Message);
            TxtGlobalPrivacy.Text = gpDisplay;

            SetStatus($"已加载 {_adapters.Count} 块网卡的协议开关");
            EmptyState.Visibility = _adapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"加载网卡协议开关失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private static string FormatGlobalPrivacy(string randomize, string tempAddr)
    {
        string r = randomize switch
        {
            "Enabled" or "1" => "开",
            "Disabled" or "0" => "关",
            _ => randomize ?? "?"
        };
        string t = tempAddr switch
        {
            "Always" => "始终",
            "Enabled" or "1" => "开",
            "Disabled" or "0" => "关",
            _ => tempAddr ?? "?"
        };
        return $"RandomizeIdentifiers={r}  UseTemporaryAddresses={t}";
    }

    private List<AdapterToggleInfo> GetSelected() =>
        ToggleGrid.SelectedItems.Cast<AdapterToggleInfo>().ToList();

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshDataAsync();

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => ToggleGrid.SelectAll();

    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.InvertSelection(ToggleGrid, _adapters);

    // ── IPv6 Binding ─────────────────────────────────────────────────

    private async void BtnEnableIPv6_Click(object sender, RoutedEventArgs e) =>
        await SetIPv6Binding(true);

    private async void BtnDisableIPv6_Click(object sender, RoutedEventArgs e) =>
        await SetIPv6Binding(false);

    private async Task SetIPv6Binding(bool enabled)
    {
        var selected = GetSelected();
        if (selected.Count == 0)
        {
            CopyableMessageBox.Show("请先选择至少一个网卡。", "未选择", MessageBoxImage.Information);
            return;
        }

        string action = enabled ? "启用" : "禁用";
        var names = string.Join("\n", selected.Select(a => $"  • {a.Name} ({a.IPv6Display})"));
        var confirm = MessageBox.Show(
            $"确定要为以下 {selected.Count} 块网卡{action} IPv6？\n\n{names}\n\n" +
            (enabled ? "启用后网卡将获取 IPv6 地址。" : "禁用后该网卡的所有 IPv6 地址将失效。"),
            $"确认{action} IPv6",
            MessageBoxButton.YesNo,
            enabled ? MessageBoxImage.Question : MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SetStatus($"正在{action} IPv6：{selected.Count} 块网卡...");
        var result = await Task.Run(() =>
        {
            var ok = new List<AdapterToggleInfo>();
            var errors = new List<string>();
            foreach (var a in selected)
            {
                var r = _manager.SetIPv6Binding(a.Name, enabled);
                if (r.Success) ok.Add(a);
                else errors.Add($"{a.Name}: {r.Message}");
            }
            return (ok, errors);
        });

        ShowBatchResult(result.ok, result.errors, selected.Count, $"IPv6 {action}",
            result.ok.Select(a => AdapterToggleManager.GetIPv6BindingCommandPreview(a.Name, enabled)));
        await AutoRefreshAfterDelay(1000);
    }

    // ── IPv6 Privacy ─────────────────────────────────────────────────

    private async void BtnEnablePrivacy_Click(object sender, RoutedEventArgs e) =>
        await SetIPv6Privacy(true);

    private async void BtnDisablePrivacy_Click(object sender, RoutedEventArgs e) =>
        await SetIPv6Privacy(false);

    private async Task SetIPv6Privacy(bool enabled)
    {
        var selected = GetSelected();
        if (selected.Count == 0)
        {
            CopyableMessageBox.Show("请先选择至少一个网卡。", "未选择", MessageBoxImage.Information);
            return;
        }

        string action = enabled ? "启用" : "禁用";
        var names = string.Join("\n", selected.Select(a => $"  • {a.Name} ({a.IPv6PrivacyDisplay})"));
        string hint = enabled
            ? "启用后系统会使用随机生成的接口 ID，不再基于 MAC 地址。需要释放续租或重启网卡后生效。"
            : "禁用后系统会使用基于 MAC 的固定 EUI-64 地址，减少地址数量。需要释放续租或重启网卡后生效。";
        var confirm = MessageBox.Show(
            $"确定要为以下 {selected.Count} 块网卡{action}隐私地址？\n\n{names}\n\n{hint}",
            $"确认{action}隐私地址",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetStatus($"正在{action}隐私地址：{selected.Count} 块网卡...");
        var result = await Task.Run(() =>
        {
            var ok = new List<AdapterToggleInfo>();
            var errors = new List<string>();
            foreach (var a in selected)
            {
                var r = _manager.SetIPv6Privacy(a.Name, enabled);
                if (r.Success) ok.Add(a);
                else errors.Add($"{a.Name}: {r.Message}");
            }
            return (ok, errors);
        });

        ShowBatchResult(result.ok, result.errors, selected.Count, $"隐私地址{action}",
            result.ok.Select(a => AdapterToggleManager.GetIPv6PrivacyCommandPreview(a.Name, enabled)));
        await AutoRefreshAfterDelay(1000);
    }

    // ── Global privacy toggles ───────────────────────────────────────

    private async void BtnGlobalPrivacyOn_Click(object sender, RoutedEventArgs e) =>
        await SetGlobalPrivacy(true);

    private async void BtnGlobalPrivacyOff_Click(object sender, RoutedEventArgs e) =>
        await SetGlobalPrivacy(false);

    private async Task SetGlobalPrivacy(bool enabled)
    {
        string action = enabled ? "启用" : "禁用";
        var confirm = MessageBox.Show(
            $"确定要全局{action} IPv6 隐私地址？\n\n" +
            $"将同时设置 RandomizeIdentifiers={( enabled ? "Enabled" : "Disabled" )} 和 " +
            $"UseTemporaryAddresses={( enabled ? "Always" : "Disabled" )}。\n\n" +
            "此操作影响所有网卡的 IPv6 默认行为，已有连接的具体接口设置优先。",
            $"确认全局{action}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetStatus($"正在全局{action} IPv6 隐私地址...");
        var result = await Task.Run(() =>
        {
            var r1 = _manager.SetGlobalIPv6Privacy(enabled);
            var r2 = _manager.SetGlobalUseTemporaryAddresses(enabled);
            return (r1, r2);
        });

        var previews = new List<string>
        {
            AdapterToggleManager.GetGlobalIPv6PrivacyCommandPreview(enabled),
            AdapterToggleManager.GetGlobalUseTemporaryAddressesCommandPreview(enabled)
        };
        if (Window.GetWindow(this) is MainWindow mw)
            mw.SetCommandPreview(string.Join("\n", previews));

        if (!result.r1.Success || !result.r2.Success)
        {
            var errors = new List<string>();
            if (!result.r1.Success) errors.Add($"RandomizeIdentifiers: {result.r1.Message}");
            if (!result.r2.Success) errors.Add($"UseTemporaryAddresses: {result.r2.Message}");
            CopyableMessageBox.Show(
                $"部分设置失败：\n{string.Join("\n", errors)}",
                "操作结果", MessageBoxImage.Warning);
        }

        SetStatus($"全局 IPv6 隐私地址已{action}");
        await AutoRefreshAfterDelay(500);
    }

    // ── NetBIOS ──────────────────────────────────────────────────────

    private async void BtnNetBiosDisable_Click(object sender, RoutedEventArgs e) =>
        await SetNetBios(2, "禁用");

    private async void BtnNetBiosEnable_Click(object sender, RoutedEventArgs e) =>
        await SetNetBios(1, "启用");

    private async void BtnNetBiosDefault_Click(object sender, RoutedEventArgs e) =>
        await SetNetBios(0, "恢复默认");

    private async Task SetNetBios(int option, string actionLabel)
    {
        var selected = GetSelected();
        if (selected.Count == 0)
        {
            CopyableMessageBox.Show("请先选择至少一个网卡。", "未选择", MessageBoxImage.Information);
            return;
        }

        var names = string.Join("\n", selected.Select(a => $"  • {a.Name} ({a.NetBiosDisplay})"));
        var confirm = MessageBox.Show(
            $"确定要为以下 {selected.Count} 块网卡{actionLabel} NetBIOS over TCP/IP？\n\n{names}",
            $"确认{actionLabel} NetBIOS",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetStatus($"正在{actionLabel} NetBIOS：{selected.Count} 块网卡...");
        var result = await Task.Run(() =>
        {
            var ok = new List<AdapterToggleInfo>();
            var errors = new List<string>();
            foreach (var a in selected)
            {
                var r = _manager.SetNetBios(a.Name, option);
                if (r.Success) ok.Add(a);
                else errors.Add($"{a.Name}: {r.Message}");
            }
            return (ok, errors);
        });

        ShowBatchResult(result.ok, result.errors, selected.Count, $"NetBIOS {actionLabel}",
            result.ok.Select(a => AdapterToggleManager.GetNetBiosCommandPreview(a.Name, option)));
        await AutoRefreshAfterDelay(500);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void ShowBatchResult(List<AdapterToggleInfo> ok, List<string> errors, int total,
        string opLabel, IEnumerable<string> previews)
    {
        if (ok.Count > 0 && Window.GetWindow(this) is MainWindow mw)
            mw.SetCommandPreview(string.Join("\n", previews));

        if (errors.Count > 0)
        {
            CopyableMessageBox.Show(
                $"成功 {ok.Count}/{total}。\n\n失败项：\n{string.Join("\n", errors)}",
                "操作结果", MessageBoxImage.Warning);
        }

        SetStatus(errors.Count == 0
            ? $"{opLabel}完成 {ok.Count}/{total}"
            : $"{opLabel}部分失败 {ok.Count}/{total}");
    }

    private async Task AutoRefreshAfterDelay(int delayMs)
    {
        await Task.Delay(delayMs);
        await RefreshDataAsync();
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.CopySelectedCellValue(ToggleGrid);

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var dir = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        e.Column.SortDirection = dir;
        var view = CollectionViewSource.GetDefaultView(ToggleGrid.ItemsSource);
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
