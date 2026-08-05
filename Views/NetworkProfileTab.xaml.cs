using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
        Loaded += async (_, _) => await RefreshDataAsync();
    }

    private async Task RefreshDataAsync()
    {
        try
        {
            // 注册表读取放后台线程；COM 调用必须在 STA 的 UI 线程执行
            var profiles = await Task.Run(() => NetworkProfileService.GetAllProfiles());
            var connectedIds = NetworkListManagerService.GetConnectedNetworkIds();

            _profiles.Clear();
            foreach (var p in profiles)
            {
                p.IsConnected = connectedIds.Contains(p.Guid);
                _profiles.Add(p);
            }
            SetStatus($"已加载 {_profiles.Count} 个网络配置文件");
            EmptyState.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"读取网络配置文件失败：\n{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private List<NetworkProfile> GetSelected() =>
        ProfileGrid.SelectedItems.Cast<NetworkProfile>().ToList();

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshDataAsync();

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => ProfileGrid.SelectAll();

    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e) =>
        InvertSelection(ProfileGrid, _profiles);

    private async void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count != 1) { CopyableMessageBox.Show("请选择一个配置文件进行重命名。"); return; }
        var p = selected[0];
        string? n = PromptInput("重命名网络配置文件", $"当前名称: {p.ProfileName}\n请输入新名称:", p.ProfileName, Window.GetWindow(this));
        if (n == null || n == p.ProfileName) return;
        try
        {
            if (!NetworkProfileService.RenameProfile(p.Guid, n))
            {
                CopyableMessageBox.Show("重命名失败：找不到注册表键。");
                return;
            }
            SetStatus($"已将 \"{p.ProfileName}\" 重命名为 \"{n}\"");
            await RefreshDataAsync();
        }
        catch (Exception ex) { CopyableMessageBox.Show($"重命名失败：\n{ex.Message}"); }
    }

    private void BtnSetPublic_Click(object sender, RoutedEventArgs e) => SetCategory(NetworkCategory.Public);
    private void BtnSetPrivate_Click(object sender, RoutedEventArgs e) => SetCategory(NetworkCategory.Private);

    private async void SetCategory(NetworkCategory cat)
    {
        var sel = GetSelected();
        if (sel.Count == 0) { CopyableMessageBox.Show("请先选中至少一个配置文件。"); return; }
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
        await RefreshDataAsync();
    }

    private async void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var sel = GetSelected().Where(p => !p.IsConnected).ToList();
        if (sel.Count == 0) { CopyableMessageBox.Show("请选中至少一个未连接的历史配置文件进行删除。\n当前连接的网络不能删除。"); return; }
        var names = string.Join("\n", sel.Select(p => $"  - {p.ProfileName}"));
        if (MessageBox.Show($"确定要删除以下 {sel.Count} 个历史网络配置文件？\n\n{names}\n\n此操作不可撤销（但可通过备份恢复）。",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        int ok = 0;
        foreach (var p in sel) { try { NetworkProfileService.DeleteProfile(p.Guid); ok++; } catch { } }
        SetStatus($"已删除 {ok}/{sel.Count} 个网络配置文件");
        await RefreshDataAsync();
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
        var currentlySelected = grid.SelectedItems.Cast<object>().ToList();
        grid.SelectedItems.Clear();
        foreach (var item in allItems)
        {
            if (item is not null && !currentlySelected.Contains(item))
                grid.SelectedItems.Add(item);
        }
    }

    /// <summary>
    /// 让右键点击表格时同步更新选中行与当前单元格，
    /// 使「复制选中值」复制的是右键点击的那一格（WPF 默认右键不更新 CurrentCell）。
    /// </summary>
    internal static void HandleRightClick(DataGrid grid, System.Windows.Input.MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(grid);
        var row = FindRowAt(grid, pos);
        if (row?.Item == null) return;

        bool keepMulti = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control
                      || (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == System.Windows.Input.ModifierKeys.Shift;
        if (!keepMulti)
        {
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(row.Item);
        }
        grid.CurrentItem = row.Item;

        var col = FindColumnAt(grid, pos);
        if (col != null)
            grid.CurrentCell = new DataGridCellInfo(row.Item, col);
    }

    private static DataGridRow? FindRowAt(DataGrid grid, Point pos)
    {
        var hit = grid.InputHitTest(pos) as DependencyObject;
        while (hit != null && hit is not DataGridRow)
            hit = VisualTreeHelper.GetParent(hit);
        return hit as DataGridRow;
    }

    private static DataGridColumn? FindColumnAt(DataGrid grid, Point pos)
    {
        var hit = grid.InputHitTest(pos) as DependencyObject;
        while (hit != null && hit is not DataGridColumnHeader)
            hit = VisualTreeHelper.GetParent(hit);
        return (hit as DataGridColumnHeader)?.Column;
    }

    internal static string? PromptInput(string title, string prompt, string defaultValue, Window? owner = null)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner ?? Application.Current.MainWindow,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["WindowBgBrush"],
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["PrimaryTextBrush"],
        };
        ThemeManager.ApplyTitleBar(dlg);

        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["PrimaryTextBrush"],
        });
        var tb = new TextBox { Text = defaultValue };
        sp.Children.Add(tb);
        var bp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => { dlg.DialogResult = true; };
        bp.Children.Add(ok);
        bp.Children.Add(new Button { Content = "取消", Width = 70, IsCancel = true });
        sp.Children.Add(bp);

        // 内容超高时滚动，避免长提示被裁剪
        var scroll = new ScrollViewer
        {
            Content = sp,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        dlg.Content = scroll;

        // 自动聚焦并全选默认值，方便直接重写
        dlg.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return dlg.ShowDialog() == true ? tb.Text.Trim() : null;
    }
}
