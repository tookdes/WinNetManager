using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinNetManager.Services;
using WinNetManager.Views;

namespace WinNetManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyTitleBar(this);
        UpdateThemeButton();
        ThemeManager.ThemeChanged += UpdateThemeButton;
        // 连接名称变更后，需要刷新所有使用 InterfaceAlias 的标签页。
        // 各标签页独立加载数据（Loaded 事件或手动刷新），没有共享数据源，
        // 所以改名后如果不主动刷新，它们会继续显示内存中的旧名称。
        ConnectionNameService.ConnectionsChanged += OnConnectionsChanged;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (RouteTab.HasPendingChanges)
        {
            var r = MessageBox.Show(
                "持久路由还有未应用的更改，关闭窗口将丢失。\n\n确定要关闭吗？",
                "未应用的更改", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnClosing(e);
    }

    private void BtnTheme_Click(object sender, RoutedEventArgs e) => ThemeManager.Toggle();

    private void UpdateThemeButton()
    {
        BtnTheme.Content = ThemeManager.IsDark ? "亮色" : "暗色";
        BtnTheme.ToolTip = ThemeManager.IsDark ? "切换为亮色主题" : "切换为暗色主题";
    }

    private async void OnConnectionsChanged()
    {
        // Rename-NetAdapter 通知网络栈后，OS 内部传播需要短暂时间
        await Task.Delay(500);
        RefreshTab(DhcpTab);
        RefreshTab(DnsTab);
        RefreshTab(MetricTab);
        RefreshTab(RouteTab);
    }

    private async void RefreshTab(UserControl tab)
    {
        if (tab is IRefreshableTab r)
            await r.RefreshAsync();
    }

    public void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    public void SetCommandPreview(string command)
    {
        TxtCommandPreview.Text = command;
        CmdPreviewExpander.IsExpanded = true;
    }

    private void BtnNcpa_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "control.exe",
                Arguments = "ncpa.cpl",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void BtnDevMgr_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "devmgmt.msc",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void BtnBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "选择备份保存位置",
            FileName = $"NetworkBackup_{DateTime.Now:yyyyMMdd_HHmmss}",
            DefaultExt = ".reg",
            Filter = "注册表文件 (*.reg)|*.reg",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };

        if (dlg.ShowDialog(this) != true) return;

        string dir = System.IO.Path.GetDirectoryName(dlg.FileName)!;
        string baseName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);

        SetStatus("正在备份网络相关配置...");
        try
        {
            var results = await Task.Run(() => BackupNetworkConfig(dir, baseName));

            int failed = results.Count(r => !r.Success);
            if (failed == 0)
            {
                var lines = string.Join("\n", results.Select(r => r.Path));
                CopyableMessageBox.Show($"备份完成，已保存到：\n\n{lines}", "备份完成",
                    MessageBoxImage.Information);
                SetStatus($"备份完成 → {dir}");
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"备份完成（成功 {results.Count - failed}/{results.Count}）：\n");
                foreach (var r in results)
                    sb.AppendLine(r.Success ? $"  ✓ {r.Path}" : $"  ✗ {r.Name}：{r.Error}");
                CopyableMessageBox.Show(sb.ToString(), "备份完成（部分失败）", MessageBoxImage.Warning);
                SetStatus($"备份完成，{failed} 项失败 → {dir}");
            }
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"备份失败：\n{ex.Message}", "错误",
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 备份网络相关配置：网络配置文件、连接名称、设备描述、TCP/IP 参数、
    /// 持久路由（注册表 + netsh）与端口转发规则。
    /// </summary>
    private static List<(string Name, string Path, bool Success, string Error)> BackupNetworkConfig(string dir, string baseName)
    {
        var results = new List<(string Name, string Path, bool Success, string Error)>();

        void TryReg(string name, string key, string suffix)
        {
            try
            {
                string path = System.IO.Path.Combine(dir, $"{baseName}_{suffix}.reg");
                if (!KeyExists(key))
                {
                    results.Add((name, $"{path}（未生成：注册表键不存在）", true, ""));
                    return;
                }
                path = RegistryBackupService.BackupKeyToPath(key, path);
                results.Add((name, path, true, ""));
            }
            catch (Exception ex) { results.Add((name, "", false, ex.Message)); }
        }

        void TryCmd(string name, string args, string suffix)
        {
            try
            {
                string path = RegistryBackupService.BackupCommandToPath(
                    "netsh", args, System.IO.Path.Combine(dir, $"{baseName}_{suffix}.txt"));
                results.Add((name, path, true, ""));
            }
            catch (Exception ex) { results.Add((name, "", false, ex.Message)); }
        }

        TryReg("网络配置文件 (NetworkList)", @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList", "NetworkList");
        TryReg("网络连接名称 (Control Network)", @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}", "NetworkControl");
        TryReg("设备描述实例编号 (Descriptions)", @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\Descriptions", "Descriptions");
        TryReg("TCP/IP 接口参数", @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", "TcpipInterfaces");
        TryReg("IPv4 持久路由", @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\PersistentRoutes", "PersistentRoutesV4");
        TryReg("IPv6 持久路由", @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\PersistentRoutes", "PersistentRoutesV6");
        TryReg("DNS NRPT 规则", @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig", "NrptRules");
        TryCmd("端口转发规则 (netsh)", "interface portproxy dump", "PortProxy");
        TryCmd("IPv4 路由配置 (netsh)", "interface ipv4 dump", "Ipv4Routes");
        TryCmd("IPv6 路由配置 (netsh)", "interface ipv6 dump", "Ipv6Routes");

        return results;
    }

    private static bool KeyExists(string hiveAndPath)
    {
        string[] parts = hiveAndPath.Split('\\');
        if (parts.Length < 2) return false;
        using var root = parts[0] switch
        {
            "HKEY_LOCAL_MACHINE" => Microsoft.Win32.Registry.LocalMachine,
            "HKEY_CURRENT_USER" => Microsoft.Win32.Registry.CurrentUser,
            "HKEY_USERS" => Microsoft.Win32.Registry.Users,
            "HKEY_CLASSES_ROOT" => Microsoft.Win32.Registry.ClassesRoot,
            _ => null
        };
        if (root == null) return false;
        string subPath = string.Join("\\", parts.Skip(1));
        using var sub = root.OpenSubKey(subPath);
        return sub != null;
    }

    private void BtnFirewall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "wf.msc",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void BtnHosts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string hostsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "drivers", "etc", "hosts");
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{hostsPath}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        bool inTextInput = IsTextInputFocus();

        if (e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            switch (e.Key)
            {
                // 以下快捷键在文本框中仍允许触发
                case System.Windows.Input.Key.R:
                    InvokeActiveTab("BtnRefresh_Click");
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.I:
                    InvokeActiveTab("BtnInvertSelection_Click");
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.N:
                    if (!TryInvokeMethod("BtnNew_Click"))
                        TryInvokeMethod("BtnAdd_Click");
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.F:
                    FocusFilter();
                    e.Handled = true;
                    break;
                // Ctrl+A 在文本框中保留给全选，不在文本框时触发表格全选
                case System.Windows.Input.Key.A:
                    if (!inTextInput)
                    {
                        InvokeActiveTab("BtnSelectAll_Click");
                        e.Handled = true;
                    }
                    break;
            }
        }
        else if (e.Key == System.Windows.Input.Key.Delete && !inTextInput)
        {
            InvokeActiveTab("BtnDelete_Click");
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.F2 && !inTextInput)
        {
            if (!TryInvokeMethod("BtnEdit_Click"))
                TryInvokeMethod("BtnRename_Click");
            e.Handled = true;
        }
    }

    private bool TryInvokeMethod(string methodName)
    {
        var active = TabControl.SelectedItem as TabItem;
        if (active?.Content is not UserControl uc) return false;

        var mi = uc.GetType().GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (mi == null) return false;

        try
        {
            mi.Invoke(uc, new object[] { this, System.Windows.RoutedEventArgs.Empty });
            return true;
        }
        catch { return false; }
    }

    private void InvokeActiveTab(string methodName)
    {
        TryInvokeMethod(methodName);
    }

    private void FocusFilter()
    {
        var active = TabControl.SelectedItem as TabItem;
        if (active?.Content is not UserControl uc) return;

        var filterBox = uc.FindName("TxtFilter") as System.Windows.Controls.TextBox;
        if (filterBox != null)
        {
            filterBox.Focus();
            filterBox.SelectAll();
        }
    }

    private static bool IsTextInputFocus()
    {
        DependencyObject? current = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
        while (current != null)
        {
            if (current is TextBox or PasswordBox)
                return true;

            if (current is ComboBox comboBox && comboBox.IsEditable)
                return true;

            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}
