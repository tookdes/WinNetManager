using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class NetworkDiagTab : UserControl
{
    private readonly ObservableCollection<TcpConnectionInfo> _connections = new();
    private readonly ObservableCollection<ListenerInfo> _listeners = new();
    private CancellationTokenSource? _diagnosticCts;
    private bool _diagnosticBusy;

    public NetworkDiagTab()
    {
        InitializeComponent();
        ConnectionGrid.ItemsSource = _connections;
        ListenerGrid.ItemsSource = _listeners;

        if (!DiagnosticBundleService.IsElevated)
        {
            ChkPacketCapture.IsChecked = false;
            ChkPacketCapture.IsEnabled = false;
            ChkPacketCapture.ToolTip = "当前进程不是管理员权限，PktMon 抓包不可用。";
        }

        Loaded += async (_, _) => await RefreshAllAsync();
    }

    private async Task RefreshAllAsync()
    {
        try
        {
            SetStatus("正在加载连接信息...");
            var (listeners, conns) = await Task.Run(() => (GetListeners(), GetEstablishedConnections()));

            _listeners.Clear();
            foreach (var l in listeners)
                _listeners.Add(l);
            EmptyListeners.Visibility = _listeners.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _connections.Clear();
            foreach (var c in conns)
                _connections.Add(c);
            EmptyState.Visibility = _connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            SetStatus($"监听 {_listeners.Count} · 已建立 {_connections.Count}");
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show($"加载连接信息失败：{ex.Message}", "错误", MessageBoxImage.Error);
        }
    }

    private static List<ListenerInfo> GetListeners()
    {
        var result = new List<ListenerInfo>();

        // TCP Listen
        string error;
        string tcpOut = ProcessRunner.RunPowerShell(
            "Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | " +
            "Select-Object LocalAddress, LocalPort, OwningProcess | " +
            "ConvertTo-Csv -NoTypeInformation",
            out error, 15000);
        AppendListeners(result, tcpOut, "TCP");

        // UDP endpoints
        string udpOut = ProcessRunner.RunPowerShell(
            "Get-NetUDPEndpoint -ErrorAction SilentlyContinue | " +
            "Select-Object LocalAddress, LocalPort, OwningProcess | " +
            "ConvertTo-Csv -NoTypeInformation",
            out error, 15000);
        AppendListeners(result, udpOut, "UDP");

        return result
            .OrderBy(l => int.TryParse(l.LocalPort, out int p) ? p : 0)
            .ThenBy(l => l.Protocol)
            .ToList();
    }

    private static void AppendListeners(List<ListenerInfo> result, string csv, string protocol)
    {
        var lines = csv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return;

        string[] headers = CsvParser.ParseLine(lines[0]);
        int idxLocalAddr = Array.IndexOf(headers, "LocalAddress");
        int idxLocalPort = Array.IndexOf(headers, "LocalPort");
        int idxPid = Array.IndexOf(headers, "OwningProcess");

        for (int i = 1; i < lines.Length; i++)
        {
            string[] values = CsvParser.ParseLine(lines[i]);
            if (values.Length < 2) continue;

            string pid = idxPid >= 0 && idxPid < values.Length ? values[idxPid] : "";
            string procName = GetProcessName(pid);

            result.Add(new ListenerInfo
            {
                Protocol = protocol,
                LocalAddress = idxLocalAddr >= 0 && idxLocalAddr < values.Length ? values[idxLocalAddr] : "",
                LocalPort = idxLocalPort >= 0 && idxLocalPort < values.Length ? values[idxLocalPort] : "",
                OwningProcess = string.IsNullOrEmpty(procName) ? $"PID {pid}" : $"{procName} ({pid})",
            });
        }
    }

    private static List<TcpConnectionInfo> GetEstablishedConnections()
    {
        var result = new List<TcpConnectionInfo>();
        string error;
        string output = ProcessRunner.RunPowerShell(
            "Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue | " +
            "Select-Object LocalAddress, LocalPort, RemoteAddress, RemotePort, State, OwningProcess | " +
            "ConvertTo-Csv -NoTypeInformation",
            out error, 15000);

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return result;

        string[] headers = CsvParser.ParseLine(lines[0]);
        int idxLocalAddr = Array.IndexOf(headers, "LocalAddress");
        int idxLocalPort = Array.IndexOf(headers, "LocalPort");
        int idxRemoteAddr = Array.IndexOf(headers, "RemoteAddress");
        int idxRemotePort = Array.IndexOf(headers, "RemotePort");
        int idxState = Array.IndexOf(headers, "State");
        int idxPid = Array.IndexOf(headers, "OwningProcess");

        for (int i = 1; i < lines.Length; i++)
        {
            string[] values = CsvParser.ParseLine(lines[i]);
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
            return Process.GetProcessById(id).ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();

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

    private void SetDiagnosticBusy(bool busy)
    {
        _diagnosticBusy = busy;

        BtnRefresh.IsEnabled = !busy;
        BtnPortTest.IsEnabled = !busy;
        BtnTracert.IsEnabled = !busy;
        BtnPathping.IsEnabled = !busy;
        BtnHealthScan.IsEnabled = !busy;
        BtnDiagnosticBundle.IsEnabled = !busy;
        TxtTarget.IsEnabled = !busy;
        TxtPort.IsEnabled = !busy;

        if (DiagnosticBundleService.IsElevated)
            ChkPacketCapture.IsEnabled = !busy;

        BtnCancelDiagnostic.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BtnCancelDiagnostic.IsEnabled = busy;
    }

    private void BtnCancelDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (!_diagnosticBusy || _diagnosticCts == null)
            return;

        BtnCancelDiagnostic.IsEnabled = false;
        SetStatus("正在取消深度诊断...");
        _diagnosticCts.Cancel();
    }

    private async void BtnDiagnosticBundle_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosticBusy)
            return;

        var owner = Window.GetWindow(this);
        string safeMachine = string.Concat(Environment.MachineName.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));

        var save = new SaveFileDialog
        {
            Title = "保存 WinNetManager 深度诊断包",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"WinNetManager-Diagnostics-{safeMachine}-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };

        if (save.ShowDialog(owner) != true)
            return;

        bool includeCapture = ChkPacketCapture.IsChecked == true;
        if (includeCapture)
        {
            var confirm = MessageBox.Show(
                "PktMon 将抓取约 10 秒网络数据（每包截取前 128 字节）。\n\n" +
                "它有助于分析 ARP/广播风暴和异常流量，但截断内容仍可能包含 DNS 查询名、TLS SNI、HTTP 请求头片段、端口和 IP/MAC。\n" +
                "确定要把抓包文件一起放进诊断包吗？",
                "确认附带抓包",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                includeCapture = false;
        }

        SetDiagnosticBusy(true);
        using var cts = new CancellationTokenSource();
        _diagnosticCts = cts;
        var progress = new Progress<string>(msg => SetStatus($"深度诊断：{msg}"));

        DiagnosticBundleResult result;
        try
        {
            result = await Task.Run(() => DiagnosticBundleService.Create(
                save.FileName,
                new DiagnosticBundleOptions
                {
                    IncludePacketCapture = includeCapture,
                    PacketCaptureSeconds = 10
                },
                progress,
                cts.Token));
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show(
                $"生成诊断包时发生未处理错误：\n{ex}",
                "深度诊断失败",
                MessageBoxImage.Error,
                owner);
            SetStatus("深度诊断失败");
            return;
        }
        finally
        {
            _diagnosticCts = null;
            SetDiagnosticBusy(false);
        }

        if (result.Cancelled)
        {
            SetStatus("深度诊断已取消");
            string partial = string.IsNullOrWhiteSpace(result.WorkingDirectory)
                ? ""
                : $"\n\n已完成的临时采集文件保留在：\n{result.WorkingDirectory}";
            CopyableMessageBox.Show(
                "已取消深度诊断。" + partial,
                "深度诊断已取消",
                MessageBoxImage.Information,
                owner);
        }
        else if (result.Success)
        {
            SetStatus($"诊断包已生成：{result.FileCount} 个文件，耗时 {result.Duration.TotalSeconds:F0} 秒");
            CopyableMessageBox.Show(
                $"深度诊断包已生成。\n\n" +
                $"文件：{result.ZipPath}\n" +
                $"采集文件：{result.FileCount}\n" +
                $"自动警告/风险：{result.WarningCount}\n" +
                $"采集步骤异常：{result.ErrorCount}\n" +
                $"耗时：{result.Duration.TotalSeconds:F1} 秒\n\n" +
                "诊断包默认只读采集，不会修改 IP、DNS、路由、防火墙或重启网卡。\n" +
                "包内可能包含 IP/MAC、SSID/BSSID、代理/VPN 地址、活动连接和网络事件，发送前请确认接收方可信。",
                "深度诊断完成",
                result.ErrorCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information,
                owner);
        }
        else
        {
            SetStatus("深度诊断包生成失败");
            CopyableMessageBox.Show(
                $"诊断包生成失败：{result.ErrorMessage}\n\n" +
                $"已采集的临时文件保留在：\n{result.WorkingDirectory}\n\n" +
                "可从该目录手动取出已完成的诊断信息。",
                "深度诊断失败",
                MessageBoxImage.Error,
                owner);
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
            items = await Task.Run(() => NetworkHealthService.GetSnapshot());
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
        ThemeManager.ApplyTitleBar(dlg);
        dlg.ShowDialog();

        int danger = items.Count(i => i.Risk == "Danger");
        int warn = items.Count(i => i.Risk == "Warn");
        SetStatus($"扫描完成：共 {items.Count} 项，危险 {danger}，警告 {warn}");
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.CopySelectedCellValue(ConnectionGrid);

    private void MenuCopyListener_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.CopySelectedCellValue(ListenerGrid);

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

public class ListenerInfo
{
    public string Protocol { get; set; } = "";
    public string LocalAddress { get; set; } = "";
    public string LocalPort { get; set; } = "";
    public string OwningProcess { get; set; } = "";
}
