using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinNetManager.Models;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class PersistentRouteTab : UserControl
{
    private readonly RoutingManager _manager;
    private List<RouteEntry> _allRoutes = new();
    private ICollectionView? _routeView;
    private readonly Dictionary<RouteEntry, RouteEntry?> _originalSnapshots = new();

    public PersistentRouteTab()
    {
        InitializeComponent();
        _manager = new RoutingManager();
        Loaded += (_, _) => LoadRoutes();
    }

    private void LoadRoutes()
    {
        try
        {
            _allRoutes = _manager.GetPersistentRoutes();
            _originalSnapshots.Clear();
            foreach (var route in _allRoutes)
                _originalSnapshots[route] = route.Clone();

            _routeView = CollectionViewSource.GetDefaultView(_allRoutes);
            _routeView.Filter = RouteFilter;
            RoutesGrid.ItemsSource = _routeView;
            UpdateCount();
            EmptyState.Visibility = _allRoutes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载路由失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool RouteFilter(object obj)
    {
        if (obj is not RouteEntry route) return false;

        var selectedFamily = (CmbAddressFamily.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (!string.IsNullOrEmpty(selectedFamily) && route.AddressFamily != selectedFamily)
            return false;

        string filter = TxtFilter.Text?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(filter)) return true;

        return (route.DestinationPrefix?.ToLowerInvariant().Contains(filter) == true)
            || (route.NextHop?.ToLowerInvariant().Contains(filter) == true)
            || (route.InterfaceAlias?.ToLowerInvariant().Contains(filter) == true)
            || (route.AddressFamily?.ToLowerInvariant().Contains(filter) == true)
            || (route.RouteMetric?.ToLowerInvariant().Contains(filter) == true);
    }

    private void UpdateCount()
    {
        int total = _allRoutes?.Count ?? 0;
        int visible = _routeView?.Cast<object>().Count() ?? 0;
        int pending = _allRoutes?.Count(r => r.Status != ChangeStatus.Unchanged) ?? 0;

        string baseText = visible == total
            ? $"共 {total} 条持久路由"
            : $"显示 {visible} / 共 {total} 条持久路由";

        TbkCount.Text = pending > 0 ? $"{baseText}  |  待更改 {pending} 项" : baseText;

        BtnApply.IsEnabled = pending > 0;
        BtnCancelChanges.IsEnabled = pending > 0;

        EmptyState.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;

        SetStatus(TbkCount.Text);
    }

    private List<RouteEntry> GetSelected() =>
        RoutesGrid.SelectedItems.Cast<RouteEntry>().ToList();

    // --- 按钮事件 ---

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadRoutes();

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => RoutesGrid.SelectAll();

    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.InvertSelection(RoutesGrid, _allRoutes);

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        List<NetInterface> interfaces;
        try { interfaces = _manager.GetInterfaces(); }
        catch { interfaces = new List<NetInterface>(); }

        var editWindow = new RouteEditWindow(null, interfaces);
        editWindow.Owner = Window.GetWindow(this);

        if (editWindow.ShowDialog() == true)
        {
            var newRoute = editWindow.Route;
            newRoute.Status = ChangeStatus.Added;
            _allRoutes.Add(newRoute);
            _originalSnapshots[newRoute] = null;
            _routeView?.Refresh();
            UpdateCount();
        }
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count != 1)
        {
            MessageBox.Show("请选择一条路由进行修改。", "未选择", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var route = selected[0];
        if (route.Status == ChangeStatus.Deleted)
        {
            MessageBox.Show("已标记删除的路由不能修改。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        List<NetInterface> interfaces;
        try { interfaces = _manager.GetInterfaces(); }
        catch { interfaces = new List<NetInterface>(); }

        var editWindow = new RouteEditWindow(route, interfaces);
        editWindow.Owner = Window.GetWindow(this);

        if (editWindow.ShowDialog() == true)
        {
            var edited = editWindow.Route;
            if (!AreEqual(route, edited))
            {
                route.AddressFamily = edited.AddressFamily;
                route.DestinationPrefix = edited.DestinationPrefix;
                route.NextHop = edited.NextHop;
                route.InterfaceAlias = edited.InterfaceAlias;
                route.RouteMetric = edited.RouteMetric;

                if (route.Status != ChangeStatus.Added)
                    route.Status = ChangeStatus.Modified;

                _routeView?.Refresh();
                UpdateCount();
            }
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择要删除的路由。", "未选择", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var route in selected)
        {
            if (route.Status == ChangeStatus.Added)
            {
                _allRoutes.Remove(route);
                _originalSnapshots.Remove(route);
            }
            else
            {
                route.Status = ChangeStatus.Deleted;
            }
        }

        _routeView?.Refresh();
        UpdateCount();
    }

    private void BtnCancelChanges_Click(object sender, RoutedEventArgs e)
    {
        var pending = _allRoutes?.Count(r => r.Status != ChangeStatus.Unchanged) ?? 0;
        if (pending == 0) return;

        var result = MessageBox.Show(
            $"确定要取消所有 {pending} 项待更改吗？未应用的修改将丢失。",
            "确认取消",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
            LoadRoutes();
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var toDelete = _allRoutes.Where(r => r.Status == ChangeStatus.Deleted).ToList();
        var toModify = _allRoutes.Where(r => r.Status == ChangeStatus.Modified).ToList();
        var toAdd = _allRoutes.Where(r => r.Status == ChangeStatus.Added).ToList();

        if (toDelete.Count == 0 && toModify.Count == 0 && toAdd.Count == 0)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("即将执行以下操作：\n");

        if (toDelete.Count > 0)
        {
            sb.AppendLine($"删除 ({toDelete.Count})：");
            foreach (var r in toDelete)
                sb.AppendLine($"  • {r.DestinationDisplay}  via  {r.NextHop}");
            sb.AppendLine();
        }

        if (toModify.Count > 0)
        {
            sb.AppendLine($"修改 ({toModify.Count})：");
            foreach (var r in toModify)
            {
                var orig = _originalSnapshots.GetValueOrDefault(r);
                sb.AppendLine($"  • {orig?.DestinationDisplay ?? r.DestinationDisplay} via {orig?.NextHop ?? r.NextHop}");
                sb.AppendLine($"    → {r.DestinationDisplay} via {r.NextHop}  (接口: {r.InterfaceAlias}, 度量: {r.RouteMetric})");
            }
            sb.AppendLine();
        }

        if (toAdd.Count > 0)
        {
            sb.AppendLine($"新增 ({toAdd.Count})：");
            foreach (var r in toAdd)
                sb.AppendLine($"  • {r.DestinationDisplay}  via  {r.NextHop}  (接口: {r.InterfaceAlias}, 度量: {r.RouteMetric})");
            sb.AppendLine();
        }

        sb.AppendLine("确定要继续吗？");

        var confirm = MessageBox.Show(sb.ToString(), "确认应用更改", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var errors = new List<string>();
        var successes = new List<string>();

        foreach (var route in toDelete)
        {
            var original = _originalSnapshots.GetValueOrDefault(route);
            if (original == null)
            {
                errors.Add($"删除 {route.DestinationDisplay}：找不到原始快照");
                continue;
            }
            var res = _manager.DeleteRoute(original);
            if (res.Success)
                successes.Add($"删除 {route.DestinationDisplay}");
            else
                errors.Add($"删除 {route.DestinationDisplay} 失败: {res.Message}");
        }

        foreach (var route in toModify)
        {
            var original = _originalSnapshots.GetValueOrDefault(route);
            if (original == null)
            {
                errors.Add($"修改 {route.DestinationDisplay}：找不到原始快照");
                continue;
            }
            var delRes = _manager.DeleteRoute(original);
            if (!delRes.Success)
            {
                errors.Add($"修改 {route.DestinationDisplay}: 无法删除原路由 - {delRes.Message}");
                continue;
            }
            var addRes = _manager.AddRoute(route);
            if (addRes.Success)
            {
                successes.Add($"修改 {route.DestinationDisplay}");
            }
            else
            {
                errors.Add($"修改 {route.DestinationDisplay}: 无法添加新路由 - {addRes.Message}");
                var restoreRes = _manager.AddRoute(original);
                if (!restoreRes.Success)
                    errors.Add($"  ⚠ 恢复原始路由也失败: {restoreRes.Message}");
            }
        }

        foreach (var route in toAdd)
        {
            var res = _manager.AddRoute(route);
            if (res.Success)
                successes.Add($"新增 {route.DestinationDisplay}");
            else
                errors.Add($"新增 {route.DestinationDisplay} 失败: {res.Message}");
        }

        ShowApplyResult(successes, errors);

        // 收集命令预览
        var cmdPreview = new StringBuilder();
        foreach (var route in toDelete)
        {
            var original = _originalSnapshots.GetValueOrDefault(route);
            if (original != null)
                cmdPreview.AppendLine(GetRouteCommand(original, isDelete: true));
        }
        foreach (var route in toModify)
        {
            var original = _originalSnapshots.GetValueOrDefault(route);
            if (original != null)
            {
                cmdPreview.AppendLine(GetRouteCommand(original, isDelete: true));
                cmdPreview.AppendLine(GetRouteCommand(route, isDelete: false));
            }
        }
        foreach (var route in toAdd)
            cmdPreview.AppendLine(GetRouteCommand(route, isDelete: false));

        if (cmdPreview.Length > 0 && Window.GetWindow(this) is MainWindow mw)
            mw.SetCommandPreview(cmdPreview.ToString().Trim());

        LoadRoutes();
    }

    private static string GetRouteCommand(RouteEntry route, bool isDelete)
    {
        string family = route.AddressFamily == "IPv6" ? "IPv6" : "IPv4";
        string safePrefix = route.DestinationPrefix.Replace("'", "''");
        string safeAlias = route.InterfaceAlias.Replace("'", "''");
        string safeHop = route.NextHop.Replace("'", "''");
        if (isDelete)
            return $"Remove-NetRoute -AddressFamily {family} -DestinationPrefix '{safePrefix}' -InterfaceAlias '{safeAlias}' -NextHop '{safeHop}' -PolicyStore PersistentStore -Confirm:$false";
        return $"New-NetRoute -AddressFamily {family} -DestinationPrefix '{safePrefix}' -InterfaceAlias '{safeAlias}' -NextHop '{safeHop}' -RouteMetric {route.RouteMetric} -PolicyStore PersistentStore";
    }

    private static void ShowApplyResult(List<string> successes, List<string> errors)
    {
        var sb = new StringBuilder();
        if (successes.Count > 0)
        {
            sb.AppendLine($"成功 {successes.Count} 项：");
            foreach (var s in successes) sb.AppendLine($"  {s}");
            sb.AppendLine();
        }
        if (errors.Count > 0)
        {
            sb.AppendLine($"失败 {errors.Count} 项：");
            foreach (var err in errors) sb.AppendLine($"  {err}");
        }
        if (successes.Count == 0 && errors.Count == 0)
            sb.AppendLine("没有需要执行的操作。");

        var icon = errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information;
        MessageBox.Show(sb.ToString(), "操作结果", MessageBoxButton.OK, icon);
    }

    // --- 筛选 ---

    private void CmbAddressFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _routeView?.Refresh();
        UpdateCount();
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _routeView?.Refresh();
        UpdateCount();
    }

    private void BtnClearFilter_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        TxtFilter.Clear();
        CmbAddressFamily.SelectedIndex = 0;
        _routeView?.Refresh();
        UpdateCount();
    }

    // --- 右键菜单 ---

    private void MenuCopy_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.CopySelectedCellValue(RoutesGrid);

    // --- 排序 ---

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var dir = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        e.Column.SortDirection = dir;
        var view = CollectionViewSource.GetDefaultView(RoutesGrid.ItemsSource);
        view.SortDescriptions.Clear();
        string prop = (e.Column as DataGridBoundColumn)?.Binding is Binding b ? b.Path.Path : "";
        view.SortDescriptions.Add(new SortDescription(prop, dir));
        if (view is ListCollectionView lcv)
            lcv.CustomSort = new NaturalSortByProperty(prop, dir);
    }

    // --- 工具 ---

    private static bool AreEqual(RouteEntry a, RouteEntry b)
    {
        if (a == null || b == null) return a == b;
        return a.AddressFamily == b.AddressFamily
            && a.DestinationPrefix == b.DestinationPrefix
            && a.NextHop == b.NextHop
            && a.InterfaceAlias == b.InterfaceAlias
            && a.InterfaceIndex == b.InterfaceIndex
            && a.RouteMetric == b.RouteMetric;
    }

    // --- 导入导出 ---

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出持久路由配置",
            FileName = $"WinNetManager_Routes_{DateTime.Now:yyyyMMdd}",
            DefaultExt = ".json",
            Filter = "JSON 文件 (*.json)|*.json",
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var currentRoutes = _manager.GetPersistentRoutes();
            var config = new WinNetConfig
            {
                PersistentRoutes = ConfigExportService.ToRouteConfigs(currentRoutes),
            };
            ConfigExportService.Export(dlg.FileName, config);

            var msg = $"已导出 {currentRoutes.Count} 条持久路由到：\n{dlg.FileName}";
            MessageBox.Show(msg, "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
            SetStatus(msg);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入持久路由配置",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var config = ConfigExportService.Import(dlg.FileName);
            var importedRoutes = ConfigExportService.ToRouteEntries(config.PersistentRoutes);

            if (importedRoutes.Count == 0)
            {
                MessageBox.Show("配置文件中未找到持久路由数据。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LoadRoutes();

            var toAdd = new List<RouteEntry>();
            var toModify = new List<(RouteEntry Existing, RouteEntry New)>();
            int skipped = 0;

            foreach (var imported in importedRoutes)
            {
                var exactMatch = _allRoutes.FirstOrDefault(r => AreEqual(r, imported));
                if (exactMatch != null)
                {
                    skipped++;
                    continue;
                }

                var keyMatch = _allRoutes.FirstOrDefault(r => MatchBusinessKey(r, imported));
                if (keyMatch != null)
                {
                    toModify.Add((keyMatch, imported));
                }
                else
                {
                    toAdd.Add(imported);
                }
            }

            if (toAdd.Count == 0 && toModify.Count == 0)
            {
                MessageBox.Show($"配置文件中所有 {skipped} 条路由均已存在，无需导入。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // --- 干运行预览 ---
            var sb = new StringBuilder();
            sb.AppendLine($"即将导入 {importedRoutes.Count} 条持久路由：\n");
            sb.AppendLine($"新增：{toAdd.Count} 条");
            sb.AppendLine($"修改：{toModify.Count} 条");
            sb.AppendLine($"跳过（已存在）：{skipped} 条\n");

            if (toAdd.Count > 0)
            {
                sb.AppendLine("新增项：");
                foreach (var r in toAdd)
                    sb.AppendLine($"  • {r.DestinationDisplay} via {r.NextHop} (接口: {r.InterfaceAlias}, 度量: {r.RouteMetric})");
                sb.AppendLine();
            }

            if (toModify.Count > 0)
            {
                sb.AppendLine("修改项：");
                foreach (var (old, nw) in toModify)
                {
                    sb.AppendLine($"  • {old.DestinationDisplay} via {old.NextHop}");
                    sb.AppendLine($"    → {nw.DestinationDisplay} via {nw.NextHop} (接口: {nw.InterfaceAlias}, 度量: {nw.RouteMetric})");
                }
                sb.AppendLine();
            }

            sb.AppendLine("确定要导入吗？导入后将标记为待更改，需点击「应用更改」写入系统。");

            var result = MessageBox.Show(sb.ToString(), "导入预览", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            // --- 执行导入 ---
            foreach (var (_, nw) in toModify)
            {
                var keyMatch = _allRoutes.FirstOrDefault(r => MatchBusinessKey(r, nw));
                if (keyMatch != null)
                {
                    keyMatch.AddressFamily = nw.AddressFamily;
                    keyMatch.DestinationPrefix = nw.DestinationPrefix;
                    keyMatch.NextHop = nw.NextHop;
                    keyMatch.InterfaceAlias = nw.InterfaceAlias;
                    keyMatch.InterfaceIndex = nw.InterfaceIndex;
                    keyMatch.RouteMetric = nw.RouteMetric;

                    if (keyMatch.Status != ChangeStatus.Added)
                        keyMatch.Status = ChangeStatus.Modified;
                }
            }

            foreach (var r in toAdd)
            {
                r.Status = ChangeStatus.Added;
                _allRoutes.Add(r);
                _originalSnapshots[r] = null;
            }

            _routeView?.Refresh();
            UpdateCount();

            var msg = $"导入完成：新增 {toAdd.Count} 条，修改 {toModify.Count} 条，跳过 {skipped} 条。";
            msg += "\n\n请点击「应用更改」将修改写入系统。";

            MessageBox.Show(msg, "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
            SetStatus(msg);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- 路由测试 ---

    private void BtnRouteTest_Click(object sender, RoutedEventArgs e)
    {
        string target = TxtTestTarget.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(target))
        {
            MessageBox.Show("请输入目标 IP 地址或域名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TxtTestResult.Text = $"正在测试到 {target} 的路由...";

        try
        {
            var sb = new StringBuilder();

            // 1. 解析域名
            string ip = target;
            if (!System.Net.IPAddress.TryParse(target, out _))
            {
                try
                {
                    var addresses = System.Net.Dns.GetHostAddresses(target);
                    if (addresses.Length == 0)
                    {
                        TxtTestResult.Text = $"无法解析域名：{target}";
                        return;
                    }
                    ip = addresses[0].ToString();
                    sb.AppendLine($"域名解析：{target} -> {ip}");
                    sb.AppendLine();
                }
                catch
                {
                    TxtTestResult.Text = $"无法解析域名：{target}";
                    return;
                }
            }

            // 2. 查找匹配路由
            string routeScript =
                $"Find-NetRoute -RemoteIPAddress '{ProcessRunner.EscapePsSingleQuoted(ip)}' | " +
                "Select-Object InterfaceAlias, InterfaceIndex, NextHop, RouteMetric | " +
                "ConvertTo-Csv -NoTypeInformation";
            string routeError;
            string routeOutput = RunPowerShell(routeScript, out routeError, 15000);

            if (!string.IsNullOrEmpty(routeError) && !routeError.Contains("警告"))
            {
                sb.AppendLine($"路由查询失败：{routeError.Trim()}");
            }
            else
            {
                var lines = routeOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length >= 2)
                {
                    string[] headers = ParseCsvLine(lines[0]);
                    int idxAlias = Array.IndexOf(headers, "InterfaceAlias");
                    int idxIdx = Array.IndexOf(headers, "InterfaceIndex");
                    int idxHop = Array.IndexOf(headers, "NextHop");
                    int idxMetric = Array.IndexOf(headers, "RouteMetric");

                    for (int i = 1; i < lines.Length; i++)
                    {
                        string[] values = ParseCsvLine(lines[i]);
                        if (values.Length < 2) continue;

                        string alias = idxAlias >= 0 && idxAlias < values.Length ? values[idxAlias] : "";
                        string idx = idxIdx >= 0 && idxIdx < values.Length ? values[idxIdx] : "";
                        string hop = idxHop >= 0 && idxHop < values.Length ? values[idxHop] : "";
                        string metric = idxMetric >= 0 && idxMetric < values.Length ? values[idxMetric] : "";

                        sb.AppendLine($"匹配路由：接口={alias} (索引 {idx})  下一跳={hop}  度量={metric}");
                    }
                }
                else
                {
                    sb.AppendLine("未找到匹配路由。");
                }
            }

            sb.AppendLine();

            // 3. Ping 测试
            string pingScript = $"Test-Connection -ComputerName '{ProcessRunner.EscapePsSingleQuoted(ip)}' -Count 4 -ErrorAction SilentlyContinue | Select-Object ResponseTime | ConvertTo-Csv -NoTypeInformation";
            string pingError;
            string pingOutput = RunPowerShell(pingScript, out pingError, 20000);

            if (!string.IsNullOrEmpty(pingError) && !pingError.Contains("警告"))
            {
                sb.AppendLine($"Ping 失败：{pingError.Trim()}");
            }
            else
            {
                var lines = pingOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var latencies = new List<int>();
                if (lines.Length >= 2)
                {
                    int idxLatency = Array.IndexOf(ParseCsvLine(lines[0]), "ResponseTime");
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string[] values = ParseCsvLine(lines[i]);
                        if (idxLatency >= 0 && idxLatency < values.Length && int.TryParse(values[idxLatency], out int lat))
                            latencies.Add(lat);
                    }
                }

                if (latencies.Count > 0)
                {
                    sb.AppendLine($"Ping 测试：发送 4，收到 {latencies.Count}");
                    sb.AppendLine($"  延迟  最小={latencies.Min()}ms  平均={(int)latencies.Average()}ms  最大={latencies.Max()}ms");
                }
                else
                {
                    sb.AppendLine("Ping 无响应（目标不可达或被防火墙拦截）。");
                }
            }

            TxtTestResult.Text = sb.ToString().Trim();
            SetStatus($"路由测试完成：{target}");
        }
        catch (Exception ex)
        {
            TxtTestResult.Text = $"测试失败：{ex.Message}";
        }
    }

    private static string RunPowerShell(string script, out string error, int timeoutMs)
        => ProcessRunner.RunPowerShell(script, out error, timeoutMs);

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
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

    // --- 复制 ---

    private void MenuCopyAllTest_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtTestResult.Text))
            Clipboard.SetText(TxtTestResult.Text);
    }

    private void MenuCopySelectedTest_Click(object sender, RoutedEventArgs e)
    {
        string text = !string.IsNullOrEmpty(TxtTestResult.SelectedText)
            ? TxtTestResult.SelectedText
            : TxtTestResult.Text;
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    // --- 工具 ---

    private static bool MatchBusinessKey(RouteEntry a, RouteEntry b)
    {
        return a.AddressFamily == b.AddressFamily
            && a.DestinationPrefix == b.DestinationPrefix
            && a.NextHop == b.NextHop
            && a.InterfaceAlias == b.InterfaceAlias;
    }

    private void SetStatus(string msg)
    {
        if (Window.GetWindow(this) is MainWindow mw) mw.SetStatus(msg);
    }
}
