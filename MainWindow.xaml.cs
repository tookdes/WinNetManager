using System.Diagnostics;
using System.Windows;
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
}
