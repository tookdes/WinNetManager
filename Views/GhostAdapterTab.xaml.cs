using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using WinNetManager.Models;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class GhostAdapterTab : UserControl
{
    private readonly ObservableCollection<GhostAdapter> _adapters = new();

    public GhostAdapterTab()
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
            foreach (var a in GhostAdapterService.GetAllNetworkAdapters()) _adapters.Add(a);
            int ghosts = _adapters.Count(a => !a.IsPresent);
            SetStatus($"已加载 {_adapters.Count} 个网络适配器（{ghosts} 个幽灵设备）");
            EmptyState.Visibility = _adapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { CopyableMessageBox.Show($"枚举网络适配器失败：\n{ex.Message}"); }
    }

    private List<GhostAdapter> GetSelected() =>
        AdapterGrid.SelectedItems.Cast<GhostAdapter>().ToList();

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshData();
    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => AdapterGrid.SelectAll();
    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.InvertSelection(AdapterGrid, _adapters);

    private void BtnProperties_Click(object sender, RoutedEventArgs e) => OpenSelectedProperties();

    private void AdapterGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        OpenSelectedProperties();
    }

    private void OpenSelectedProperties()
    {
        var sel = GetSelected();
        if (sel.Count == 0)
        {
            CopyableMessageBox.Show("请先选中一个网卡设备。", "未选择", MessageBoxImage.Information);
            return;
        }
        if (sel.Count > 1)
        {
            CopyableMessageBox.Show("请只选中一个网卡设备打开属性。", "多选", MessageBoxImage.Information);
            return;
        }

        var adapter = sel[0];
        if (string.IsNullOrWhiteSpace(adapter.DeviceInstanceId))
        {
            CopyableMessageBox.Show("该设备缺少实例 ID，无法打开属性。", "错误", MessageBoxImage.Warning);
            return;
        }

        try
        {
            GhostAdapterService.OpenDeviceProperties(adapter.DeviceInstanceId);
            SetStatus($"已打开「{adapter.FriendlyName}」的设备属性");
            if (Window.GetWindow(this) is MainWindow mw)
            {
                mw.SetCommandPreview(
                    $"rundll32.exe devmgr.dll,DeviceProperties_RunDLL /DeviceID \"{adapter.DeviceInstanceId}\"");
            }
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"打开设备属性失败：\n{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        var sel = GetSelected().Where(a => !a.IsPresent).ToList();
        if (sel.Count == 0) { CopyableMessageBox.Show("请选中要卸载的幽灵设备。\n活跃设备不能通过此方式卸载。\n\n注意：WAN Miniport 等系统虚拟设备不应卸载。"); return; }
        var names = string.Join("\n", sel.Select(a => $"  - {a.FriendlyName} ({a.DeviceInstanceId})"));
        if (MessageBox.Show($"确定要卸载以下 {sel.Count} 个幽灵设备？\n\n{names}\n\n此操作会从设备管理器中移除这些设备。",
            "确认卸载", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        int ok = 0, fail = 0;
        foreach (var a in sel)
        {
            try { if (GhostAdapterService.RemoveDevice(a.DeviceInstanceId)) ok++; else fail++; }
            catch { fail++; }
        }
        string msg = $"卸载完成：成功 {ok}，失败 {fail}";
        CopyableMessageBox.Show(msg, "卸载结果", fail > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        SetStatus(msg);
        RefreshData();
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e) => NetworkProfileTab.CopySelectedCellValue(AdapterGrid);

    private void SetStatus(string msg) { if (Window.GetWindow(this) is MainWindow mw) mw.SetStatus(msg); }
}
