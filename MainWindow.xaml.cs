using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinNetManager.Services;

namespace WinNetManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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

    private void BtnBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "选择注册表备份保存位置",
            FileName = $"NetworkBackup_{DateTime.Now:yyyyMMdd_HHmmss}",
            DefaultExt = ".reg",
            Filter = "注册表文件 (*.reg)|*.reg",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };

        if (dlg.ShowDialog(this) != true) return;

        string dir = System.IO.Path.GetDirectoryName(dlg.FileName)!;
        string baseName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);

        try
        {
            var results = new List<string>();
            results.Add(RegistryBackupService.BackupKeyToPath(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList",
                System.IO.Path.Combine(dir, $"{baseName}_NetworkList.reg")));
            results.Add(RegistryBackupService.BackupKeyToPath(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}",
                System.IO.Path.Combine(dir, $"{baseName}_NetworkControl.reg")));

            MessageBox.Show($"备份完成，已保存到：\n\n{string.Join("\n", results)}", "备份完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
            SetStatus($"备份完成 → {dir}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"备份失败：\n{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
