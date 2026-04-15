using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using WinNetManager.Models;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class DeviceDescriptionTab : UserControl
{
    private readonly ObservableCollection<DeviceDescription> _descriptions = new();

    public DeviceDescriptionTab()
    {
        InitializeComponent();
        DescGrid.ItemsSource = _descriptions;
        Loaded += (_, _) => RefreshData();
    }

    private void RefreshData()
    {
        try
        {
            _descriptions.Clear();
            foreach (var d in DeviceDescriptionService.GetAllDescriptions()) _descriptions.Add(d);
            SetStatus($"已加载 {_descriptions.Count} 个设备描述");
        }
        catch (Exception ex) { MessageBox.Show($"读取设备描述失败：\n{ex.Message}"); }
    }

    private List<DeviceDescription> GetSelected() =>
        DescGrid.SelectedItems.Cast<DeviceDescription>().ToList();

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshData();
    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => DescGrid.SelectAll();
    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.InvertSelection(DescGrid, _descriptions);

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        var sel = GetSelected();
        if (sel.Count == 0) { MessageBox.Show("请先选中至少一个设备描述进行重置。"); return; }
        var names = string.Join("\n", sel.Select(d => $"  - {d.Name} ({d.InstanceCountDisplay})"));
        if (MessageBox.Show($"确定要将以下 {sel.Count} 个设备描述重置为单实例？\n\n{names}\n\n重置后需要重新插拔网卡或重启生效。",
            "确认重置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        int ok = 0;
        foreach (var d in sel) { try { DeviceDescriptionService.ResetCounter(d.Name); ok++; } catch { } }
        SetStatus($"已重置 {ok}/{sel.Count} 个设备描述");
        RefreshData();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var sel = GetSelected();
        if (sel.Count == 0) { MessageBox.Show("请先选中至少一个设备描述进行删除。"); return; }
        var names = string.Join("\n", sel.Select(d => $"  - {d.Name}"));
        if (MessageBox.Show($"确定要删除以下 {sel.Count} 个设备描述条目？\n\n{names}\n\n此操作不可撤销（但可通过备份恢复）。",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        int ok = 0;
        foreach (var d in sel) { try { DeviceDescriptionService.DeleteDescription(d.Name); ok++; } catch { } }
        SetStatus($"已删除 {ok}/{sel.Count} 个设备描述条目");
        RefreshData();
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e) => NetworkProfileTab.CopySelectedCellValue(DescGrid);

    private void SetStatus(string msg) { if (Window.GetWindow(this) is MainWindow mw) mw.SetStatus(msg); }
}
