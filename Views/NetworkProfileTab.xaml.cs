using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using WinNetManager.Models;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class NetworkProfileTab : UserControl
{
    private readonly ObservableCollection<NetworkProfile> _profiles = new();

    public NetworkProfileTab()
    {
        InitializeComponent();
        ProfileGrid.ItemsSource = _profiles;
        Loaded += (_, _) => RefreshData();
    }

    private void RefreshData()
    {
        try
        {
            _profiles.Clear();
            var profiles = NetworkProfileService.GetAllProfiles();
            var connectedIds = NetworkListManagerService.GetConnectedNetworkIds();
            foreach (var p in profiles)
            {
                p.IsConnected = connectedIds.Contains(p.Guid);
                _profiles.Add(p);
            }
            SetStatus($"已加载 {_profiles.Count} 个网络配置文件");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"读取网络配置文件失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private List<NetworkProfile> GetSelected() =>
        ProfileGrid.SelectedItems.Cast<NetworkProfile>().ToList();

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshData();

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => ProfileGrid.SelectAll();

    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e) =>
        InvertSelection(ProfileGrid, _profiles);

    private void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count != 1) { MessageBox.Show("请选择一个配置文件进行重命名。"); return; }
        var p = selected[0];
        string? n = PromptInput("重命名网络配置文件", $"当前名称: {p.ProfileName}\n请输入新名称:", p.ProfileName);
        if (n == null || n == p.ProfileName) return;
        try
        {
            NetworkProfileService.RenameProfile(p.Guid, n);
            SetStatus($"已将 \"{p.ProfileName}\" 重命名为 \"{n}\"");
            RefreshData();
        }
        catch (Exception ex) { MessageBox.Show($"重命名失败：\n{ex.Message}"); }
    }

    private void BtnSetPublic_Click(object sender, RoutedEventArgs e) => SetCategory(NetworkCategory.Public);
    private void BtnSetPrivate_Click(object sender, RoutedEventArgs e) => SetCategory(NetworkCategory.Private);

    private void SetCategory(NetworkCategory cat)
    {
        var sel = GetSelected();
        if (sel.Count == 0) { MessageBox.Show("请先选中至少一个配置文件。"); return; }
        string cn = cat == NetworkCategory.Public ? "公用" : "专用";
        int ok = 0;
        foreach (var p in sel)
        {
            try
            {
                if (p.IsConnected) NetworkListManagerService.SetCategoryForConnectedNetwork(p.Guid, cat);
                else NetworkProfileService.SetCategory(p.Guid, cat);
                ok++;
            }
            catch { }
        }
        SetStatus($"已将 {ok}/{sel.Count} 个网络设为{cn}");
        RefreshData();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var sel = GetSelected().Where(p => !p.IsConnected).ToList();
        if (sel.Count == 0) { MessageBox.Show("请选中至少一个未连接的历史配置文件进行删除。\n当前连接的网络不能删除。"); return; }
        var names = string.Join("\n", sel.Select(p => $"  - {p.ProfileName}"));
        if (MessageBox.Show($"确定要删除以下 {sel.Count} 个历史网络配置文件？\n\n{names}\n\n此操作不可撤销（但可通过备份恢复）。",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        int ok = 0;
        foreach (var p in sel) { try { NetworkProfileService.DeleteProfile(p.Guid); ok++; } catch { } }
        SetStatus($"已删除 {ok}/{sel.Count} 个网络配置文件");
        RefreshData();
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e) => CopySelectedCellValue(ProfileGrid);

    private void MenuOpenRegEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileGrid.SelectedItem is NetworkProfile p)
            RegEditNavigator.OpenAt($@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles\{p.Guid:B}");
    }

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var dir = e.Column.SortDirection == System.ComponentModel.ListSortDirection.Ascending
            ? System.ComponentModel.ListSortDirection.Descending
            : System.ComponentModel.ListSortDirection.Ascending;
        e.Column.SortDirection = dir;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(ProfileGrid.ItemsSource);
        view.SortDescriptions.Clear();
        string prop = (e.Column as DataGridBoundColumn)?.Binding is System.Windows.Data.Binding b ? b.Path.Path : "";
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(prop, dir));
        if (view is System.Windows.Data.ListCollectionView lcv)
            lcv.CustomSort = new NaturalSortByProperty(prop, dir);
    }

    private void SetStatus(string msg) { if (Window.GetWindow(this) is MainWindow mw) mw.SetStatus(msg); }

    internal static void CopySelectedCellValue(DataGrid grid)
    {
        if (grid.CurrentCell.Column == null || grid.CurrentItem == null) return;
        var binding = (grid.CurrentCell.Column as DataGridBoundColumn)?.Binding as System.Windows.Data.Binding;
        if (binding?.Path?.Path is string path)
        {
            var prop = grid.CurrentItem.GetType().GetProperty(path);
            string? val = prop?.GetValue(grid.CurrentItem)?.ToString();
            if (!string.IsNullOrEmpty(val)) Clipboard.SetText(val);
        }
    }

    internal static void InvertSelection<T>(DataGrid grid, IList<T> allItems)
    {
        var currentlySelected = new HashSet<T>(grid.SelectedItems.Cast<T>());
        grid.SelectedItems.Clear();
        foreach (var item in allItems)
        {
            if (!currentlySelected.Contains(item))
                grid.SelectedItems.Add(item);
        }
    }

    internal static string? PromptInput(string title, string prompt, string defaultValue)
    {
        var dlg = new Window { Title = title, Width = 400, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox { Text = defaultValue };
        sp.Children.Add(tb);
        var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => { dlg.DialogResult = true; };
        bp.Children.Add(ok);
        bp.Children.Add(new Button { Content = "取消", Width = 70, IsCancel = true });
        sp.Children.Add(bp);
        dlg.Content = sp;
        return dlg.ShowDialog() == true ? tb.Text.Trim() : null;
    }
}
