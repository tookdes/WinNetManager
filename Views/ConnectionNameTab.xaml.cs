using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using WinNetManager.Models;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class ConnectionNameTab : UserControl
{
    private readonly ObservableCollection<ConnectionInfo> _connections = new();

    public ConnectionNameTab()
    {
        InitializeComponent();
        ConnectionGrid.ItemsSource = _connections;
        Loaded += async (_, _) => await RefreshDataAsync();
    }

    private async Task RefreshDataAsync()
    {
        try
        {
            var connections = await Task.Run(() => ConnectionNameService.GetAllConnections());
            _connections.Clear();
            foreach (var c in connections) _connections.Add(c);
            SetStatus($"已加载 {_connections.Count} 个连接");
            EmptyState.Visibility = _connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { CopyableMessageBox.Show($"读取连接信息失败：\n{ex.Message}", "错误", MessageBoxImage.Error); }
    }

    private List<ConnectionInfo> GetSelected() =>
        ConnectionGrid.SelectedItems.Cast<ConnectionInfo>().ToList();

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshDataAsync();
    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => ConnectionGrid.SelectAll();
    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.InvertSelection(ConnectionGrid, _connections);

    private async void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        var sel = GetSelected();
        if (sel.Count != 1) { CopyableMessageBox.Show("请选择一个连接进行重命名。"); return; }
        var c = sel[0];
        string? n = NetworkProfileTab.PromptInput("重命名连接", $"当前名称: {c.Name}\n请输入新名称:", c.Name);
        if (n == null || n == c.Name) return;
        try
        {
            var (ok, msg) = ConnectionNameService.RenameConnection(c.Guid, n);
            if (!ok)
            {
                CopyableMessageBox.Show($"重命名失败：\n{msg}", "错误", MessageBoxImage.Error);
                return;
            }
            SetStatus(msg);
            await RefreshDataAsync();
            // 通知其他标签页刷新：它们缓存了旧的 InterfaceAlias，需要重新查询
            ConnectionNameService.RaiseConnectionsChanged();
        }
        catch (Exception ex) { CopyableMessageBox.Show($"重命名失败：\n{ex.Message}", "错误", MessageBoxImage.Error); }
    }

    private async void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var sel = GetSelected().Where(c => !c.HasActiveAdapter).ToList();
        if (sel.Count == 0) { CopyableMessageBox.Show("请选中至少一个无设备的连接条目进行删除。\n有活跃适配器的连接不能删除。"); return; }
        var names = string.Join("\n", sel.Select(c => $"  - {c.Name}"));
        if (MessageBox.Show($"确定要删除以下 {sel.Count} 个连接条目？\n\n{names}\n\n此操作不可撤销（但可通过备份恢复）。",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        int ok = 0;
        foreach (var c in sel) { try { ConnectionNameService.DeleteConnection(c.Guid); ok++; } catch { } }
        SetStatus($"已删除 {ok}/{sel.Count} 个连接条目");
        await RefreshDataAsync();
        ConnectionNameService.RaiseConnectionsChanged(); // 同上，通知其他标签页
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e) => NetworkProfileTab.CopySelectedCellValue(ConnectionGrid);

    private void MenuOpenRegEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionGrid.SelectedItem is ConnectionInfo c)
            RegEditNavigator.OpenAt($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Network\{{4D36E972-E325-11CE-BFC1-08002BE10318}}\{c.Guid:B}\Connection");
    }

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var dir = e.Column.SortDirection == System.ComponentModel.ListSortDirection.Ascending
            ? System.ComponentModel.ListSortDirection.Descending
            : System.ComponentModel.ListSortDirection.Ascending;
        e.Column.SortDirection = dir;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(ConnectionGrid.ItemsSource);
        view.SortDescriptions.Clear();
        string prop = (e.Column as DataGridBoundColumn)?.Binding is System.Windows.Data.Binding b ? b.Path.Path : "";
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(prop, dir));
        if (view is System.Windows.Data.ListCollectionView lcv)
            lcv.CustomSort = new NaturalSortByProperty(prop, dir);
    }

    private void SetStatus(string msg) { if (Window.GetWindow(this) is MainWindow mw) mw.SetStatus(msg); }
}
