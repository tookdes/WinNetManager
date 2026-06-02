using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class NetworkDiagTab : UserControl
{
    private readonly ObservableCollection<TcpConnectionInfo> _connections = new();

    public NetworkDiagTab()
    {
        InitializeComponent();
        ConnectionGrid.ItemsSource = _connections;
        Loaded += async (_, _) => await RefreshConnectionsAsync();
    }

    private async Task RefreshConnectionsAsync()
    {
        try
        {
            var conns = await Task.Run(() => GetTcpConnections());
            _connections.Clear();
            foreach (var c in conns)
                _connections.Add(c);
            SetStatus($"已加载 {_connections.Count} 条 TCP 连接");
            EmptyState.Visibility = _connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"加载连接信息失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private void RefreshConnections()
    {
        try
        {
            _connections.Clear();
            foreach (var c in GetTcpConnections())
                _connections.Add(c);
            SetStatus($"已加载 {_connections.Count} 条 TCP 连接");
            EmptyState.Visibility = _connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"加载连接信息失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private static List<TcpConnectionInfo> GetTcpConnections()
    {
        var result = new List<TcpConnectionInfo>();
        string error;
        string output = RunPowerShell(
            "Get-NetTCPConnection | Where-Object { $_.State -eq 'Listen' -or $_.State -eq 'Established' } | " +
            "Select-Object LocalAddress, LocalPort, RemoteAddress, RemotePort, State, OwningProcess | " +
            "ConvertTo-Csv -NoTypeInformation",
            out error, 15000);

        // stderr 可能包含空行或无害提示；只要 stdout 有数据就继续解析
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return result;

        string[] headers = ParseCsvLine(lines[0]);
        int idxLocalAddr = Array.IndexOf(headers, "LocalAddress");
        int idxLocalPort = Array.IndexOf(headers, "LocalPort");
        int idxRemoteAddr = Array.IndexOf(headers, "RemoteAddress");
        int idxRemotePort = Array.IndexOf(headers, "RemotePort");
        int idxState = Array.IndexOf(headers, "State");
        int idxPid = Array.IndexOf(headers, "OwningProcess");

        for (int i = 1; i < lines.Length; i++)
        {
            string[] values = ParseCsvLine(lines[i]);
            if (values.Length < 3) continue;

            string pid = idxPid >= 0 && idxPid < values.Length ? values[idxPid] : "";
            string procName = GetProcessName(pid);

            result.Add(new TcpConnectionInfo
            {
                LocalAddress = idxLocalAddr >= 0 && idxLocalAddr < values.Length ? values[idxLocalAddr] : "",
                LocalPort = idxLocalPort >= 0 && idxLocalPort < values.Length ? values[idxLocalPort] : "",
                RemoteAddress = idxRemoteAddr >= 0 && idxRemoteAddr < values.Length ? values[idxRemoteAddr] : "",
                RemotePort = idxRemotePort >= 0 && idxRemotePort < values.Length ? values[idxRemotePort] : "",
                State = idxState >= 0 && idxState < values.Length ? values[idxState] : "",
                OwningProcess = string.IsNullOrEmpty(procName) ? $"PID {pid}" : $"{procName} ({pid})",
            });
        }

        return result;
    }

    private static string GetProcessName(string pid)
    {
        if (!int.TryParse(pid, out int id) || id <= 0) return "";
        try
        {
            var proc = Process.GetProcessById(id);
            return proc.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    // --- 按钮事件 ---

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshConnectionsAsync();

    private void BtnPortTest_Click(object sender, RoutedEventArgs e)
    {
        string target = TxtTarget.Text?.Trim() ?? "";
        string portText = TxtPort.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(target))
        {
            CopyableMessageBox.Show("请输入目标地址。", "提示", MessageBoxImage.Information);
            return;
        }

        if (!int.TryParse(portText, out int port) || port <= 0 || port > 65535)
        {
            CopyableMessageBox.Show("请输入有效的端口号（1-65535）。", "提示", MessageBoxImage.Information);
            return;
        }

        try
        {
            string safeTarget = ProcessRunner.EscapePsSingleQuoted(target);
            ProcessRunner.StartPowerShellWindow(
                $"Write-Host 'Test-NetConnection to {safeTarget}:{port}'; " +
                $"Test-NetConnection -ComputerName '{safeTarget}' -Port {port} -WarningAction SilentlyContinue");
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"启动失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private void BtnTracert_Click(object sender, RoutedEventArgs e)
    {
        string target = TxtTarget.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(target))
        {
            CopyableMessageBox.Show("请输入目标地址。", "提示", MessageBoxImage.Information);
            return;
        }

        try
        {
            string safeTarget = ProcessRunner.EscapePsSingleQuoted(target);
            ProcessRunner.StartPowerShellWindow(
                $"Write-Host 'Tracert to {safeTarget}'; tracert '{safeTarget}'");
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"启动失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private void BtnPathping_Click(object sender, RoutedEventArgs e)
    {
        string target = TxtTarget.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(target))
        {
            CopyableMessageBox.Show("请输入目标地址。", "提示", MessageBoxImage.Information);
            return;
        }

        try
        {
            string safeTarget = ProcessRunner.EscapePsSingleQuoted(target);
            ProcessRunner.StartPowerShellWindow(
                $"Write-Host 'Pathping to {safeTarget} (约需 25 秒)...'; pathping '{safeTarget}'");
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"启动失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private async void BtnHealthScan_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn != null) btn.IsEnabled = false;

        SetStatus("正在扫描网络健康状态...");
        BtnRefresh.IsEnabled = false;

        List<NetworkHealthItem> items;
        try
        {
            items = await System.Threading.Tasks.Task.Run(() => NetworkHealthService.GetSnapshot());
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"扫描失败：{ex.Message}", "错误", MessageBoxImage.Error);
            SetStatus("扫描失败");
            return;
        }
        finally
        {
            BtnRefresh.IsEnabled = true;
            if (btn != null) btn.IsEnabled = true;
        }

        var dlg = new NetworkHealthWindow(items)
        {
            Owner = Window.GetWindow(this)
        };
        dlg.ShowDialog();

        int danger = items.Count(i => i.Risk == "Danger");
        int warn = items.Count(i => i.Risk == "Warn");
        SetStatus($"扫描完成：共 {items.Count} 项，危险 {danger}，警告 {warn}");
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.CopySelectedCellValue(ConnectionGrid);

    // --- 工具 ---

    private static string RunPowerShell(string script, out string error, int timeoutMs)
        => ProcessRunner.RunPowerShell(script, out error, timeoutMs);

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        result.Add(sb.ToString().Trim());
        return result.ToArray();
    }

    private void SetStatus(string msg)
    {
        if (Window.GetWindow(this) is MainWindow mw) mw.SetStatus(msg);
    }
}

public class TcpConnectionInfo
{
    public string LocalAddress { get; set; } = "";
    public string LocalPort { get; set; } = "";
    public string RemoteAddress { get; set; } = "";
    public string RemotePort { get; set; } = "";
    public string State { get; set; } = "";
    public string OwningProcess { get; set; } = "";
}
