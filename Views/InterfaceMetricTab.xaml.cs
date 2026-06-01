using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinNetManager.Models;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class InterfaceMetricTab : UserControl
{
    private readonly InterfaceMetricManager _manager = new();
    private List<InterfaceMetricInfo> _allMetrics = new();
    private List<GatewayMetricInfo> _allGateways = new();
    private ICollectionView? _view;
    private ICollectionView? _gatewayView;

    public InterfaceMetricTab()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        try
        {
            var (metrics, gateways) = await Task.Run(() => (_manager.GetMetrics(), _manager.GetGatewayMetrics()));
            _allMetrics = metrics;
            _view = CollectionViewSource.GetDefaultView(_allMetrics);
            _view.Filter = MetricFilter;
            _view.SortDescriptions.Add(new SortDescription("InterfaceMetric", ListSortDirection.Ascending));
            MetricGrid.ItemsSource = _view;

            _allGateways = gateways;
            _gatewayView = CollectionViewSource.GetDefaultView(_allGateways);
            _gatewayView.Filter = GatewayFilter;
            if (_gatewayView is ListCollectionView lcvGw)
                lcvGw.CustomSort = new NaturalSortByProperty("RouteMetric", ListSortDirection.Ascending);
            else
                _gatewayView.SortDescriptions.Add(new SortDescription("RouteMetric", ListSortDirection.Ascending));
            GatewayGrid.ItemsSource = _gatewayView;

            UpdateCount();
            EmptyState.Visibility = (_allMetrics.Count == 0 && _allGateways.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载网卡跃点信息失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadData()
    {
        try
        {
            _allMetrics = _manager.GetMetrics();
            _view = CollectionViewSource.GetDefaultView(_allMetrics);
            _view.Filter = MetricFilter;
            _view.SortDescriptions.Add(new SortDescription("InterfaceMetric", ListSortDirection.Ascending));
            MetricGrid.ItemsSource = _view;

            _allGateways = _manager.GetGatewayMetrics();
            _gatewayView = CollectionViewSource.GetDefaultView(_allGateways);
            _gatewayView.Filter = GatewayFilter;
            if (_gatewayView is ListCollectionView lcvGw)
                lcvGw.CustomSort = new NaturalSortByProperty("RouteMetric", ListSortDirection.Ascending);
            else
                _gatewayView.SortDescriptions.Add(new SortDescription("RouteMetric", ListSortDirection.Ascending));
            GatewayGrid.ItemsSource = _gatewayView;

            UpdateCount();
            EmptyState.Visibility = (_allMetrics.Count == 0 && _allGateways.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载网卡跃点信息失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool MetricFilter(object obj)
    {
        if (obj is not InterfaceMetricInfo item) return false;
        return ApplyFilter(item.AddressFamily, item.InterfaceAlias, item.InterfaceIndex);
    }

    private bool GatewayFilter(object obj)
    {
        if (obj is not GatewayMetricInfo item) return false;
        return ApplyFilter(item.AddressFamily, item.InterfaceAlias, item.InterfaceIndex, item.NextHop);
    }

    private bool ApplyFilter(string family, string alias, string index, string extra = "")
    {
        var selectedFamily = (CmbAddressFamily.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (!string.IsNullOrEmpty(selectedFamily) && family != selectedFamily)
            return false;

        string filter = TxtFilter.Text?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(filter)) return true;

        return (alias?.ToLowerInvariant().Contains(filter) == true)
            || (family?.ToLowerInvariant().Contains(filter) == true)
            || (index?.ToLowerInvariant().Contains(filter) == true)
            || (extra?.ToLowerInvariant().Contains(filter) == true);
    }

    private void UpdateCount()
    {
        int total = _allMetrics?.Count ?? 0;
        int visible = _view?.Cast<object>().Count() ?? 0;
        TbkCount.Text = visible == total ? $"共 {total} 条" : $"显示 {visible} / 共 {total} 条";

        int totalGw = _allGateways?.Count ?? 0;
        int visibleGw = _gatewayView?.Cast<object>().Count() ?? 0;
        TbkGatewayCount.Text = visibleGw == totalGw ? $"共 {totalGw} 条" : $"显示 {visibleGw} / 共 {totalGw} 条";

        EmptyState.Visibility = (visible == 0 && visibleGw == 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<InterfaceMetricInfo> GetSelected() =>
        MetricGrid.SelectedItems.Cast<InterfaceMetricInfo>().ToList();

    private List<GatewayMetricInfo> GetSelectedGateways() =>
        GatewayGrid.SelectedItems.Cast<GatewayMetricInfo>().ToList();

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadDataAsync();

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => MetricGrid.SelectAll();

    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.InvertSelection(MetricGrid, _allMetrics);

    private async void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择至少一个网卡。", "未选择", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? input = NetworkProfileTab.PromptInput(
            "修改跃点数",
            $"为 {selected.Count} 个选中的网卡设置跃点数：\n" +
            string.Join("\n", selected.Select(s => $"  • {s.InterfaceAlias} ({s.AddressFamily})")),
            "10",
            Window.GetWindow(this));

        if (input == null) return;

        if (!int.TryParse(input, out int metric) || metric < 0)
        {
            MessageBox.Show("请输入有效的非负整数跃点数。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var results = await Task.Run(() =>
        {
            var list = new List<(InterfaceMetricInfo item, MetricResult result)>();
            foreach (var item in selected)
                list.Add((item, _manager.SetMetric(item.InterfaceAlias, item.AddressFamily, metric)));
            return list;
        });

        var errors = results.Where(r => !r.result.Success)
            .Select(r => $"{r.item.InterfaceAlias} ({r.item.AddressFamily}): {r.result.Message}").ToList();
        var previews = results.Where(r => r.result.Success)
            .Select(r => InterfaceMetricManager.GetSetMetricCommandPreview(r.item.InterfaceAlias, r.item.AddressFamily, metric)).ToList();

        if (previews.Count > 0)
        {
            if (Window.GetWindow(this) is MainWindow mw)
                mw.SetCommandPreview(string.Join("\n", previews));
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(
                $"成功 {selected.Count - errors.Count}/{selected.Count} 个。\n\n失败项：\n{string.Join("\n", errors)}",
                "操作结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await LoadDataAsync();
        SetStatus($"跃点数已设置为 {metric}，{selected.Count - errors.Count}/{selected.Count} 成功");
    }

    private async void BtnAuto_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择至少一个网卡。", "未选择", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var names = string.Join("\n", selected.Select(s => $"  • {s.InterfaceAlias} ({s.AddressFamily})"));
        var result = MessageBox.Show(
            $"确定要为以下 {selected.Count} 个网卡启用自动跃点？\n\n{names}",
            "确认设为自动跃点",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        var results = await Task.Run(() =>
        {
            var list = new List<(InterfaceMetricInfo item, MetricResult result)>();
            foreach (var item in selected)
                list.Add((item, _manager.SetAutoMetric(item.InterfaceAlias, item.AddressFamily)));
            return list;
        });

        var errors = results.Where(r => !r.result.Success)
            .Select(r => $"{r.item.InterfaceAlias} ({r.item.AddressFamily}): {r.result.Message}").ToList();
        var previews = results.Where(r => r.result.Success)
            .Select(r => InterfaceMetricManager.GetSetAutoMetricCommandPreview(r.item.InterfaceAlias, r.item.AddressFamily)).ToList();

        if (previews.Count > 0)
        {
            if (Window.GetWindow(this) is MainWindow mw)
                mw.SetCommandPreview(string.Join("\n", previews));
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(
                $"成功 {selected.Count - errors.Count}/{selected.Count} 个。\n\n失败项：\n{string.Join("\n", errors)}",
                "操作结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await LoadDataAsync();
        SetStatus($"已设为自动跃点，{selected.Count - errors.Count}/{selected.Count} 成功");
    }

    private async void BtnEditGateway_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedGateways();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择至少一个网关。", "未选择", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? input = NetworkProfileTab.PromptInput(
            "修改路由跃点数",
            $"为 {selected.Count} 个默认网关设置新的路由跃点数：\n" +
            string.Join("\n", selected.Select(s => $"  • {s.NextHop} ({s.InterfaceAlias})")),
            "1",
            Window.GetWindow(this));

        if (input == null) return;

        if (!int.TryParse(input, out int metric) || metric < 0)
        {
            MessageBox.Show("请输入有效的非负整数跃点数。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var results = await Task.Run(() =>
        {
            var list = new List<(GatewayMetricInfo item, MetricResult result)>();
            foreach (var item in selected)
                list.Add((item, _manager.SetGatewayMetric(item.InterfaceAlias, item.AddressFamily, item.NextHop, metric)));
            return list;
        });

        var errors = results.Where(r => !r.result.Success)
            .Select(r => $"{r.item.NextHop} ({r.item.InterfaceAlias}): {r.result.Message}").ToList();
        var previews = results.Where(r => r.result.Success)
            .Select(r => InterfaceMetricManager.GetSetGatewayMetricCommandPreview(r.item.InterfaceAlias, r.item.AddressFamily, r.item.NextHop, metric)).ToList();

        if (previews.Count > 0)
        {
            if (Window.GetWindow(this) is MainWindow mw)
                mw.SetCommandPreview(string.Join("\n", previews));
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(
                $"成功 {selected.Count - errors.Count}/{selected.Count} 个。\n\n失败项：\n{string.Join("\n", errors)}",
                "操作结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await LoadDataAsync();
        SetStatus($"网关路由跃点数已设置为 {metric}，{selected.Count - errors.Count}/{selected.Count} 成功");
    }

    private void CmbAddressFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _view?.Refresh();
        _gatewayView?.Refresh();
        UpdateCount();
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _view?.Refresh();
        _gatewayView?.Refresh();
        UpdateCount();
    }

    private void BtnClearFilter_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        TxtFilter.Clear();
        CmbAddressFamily.SelectedIndex = 0;
        _view?.Refresh();
        _gatewayView?.Refresh();
        UpdateCount();
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.CopySelectedCellValue(MetricGrid);

    private void MenuCopyGateway_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.CopySelectedCellValue(GatewayGrid);

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        HandleSorting(MetricGrid, e);
    }

    private void GatewayGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        HandleSorting(GatewayGrid, e);
    }

    private void HandleSorting(DataGrid grid, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var dir = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        e.Column.SortDirection = dir;
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        view.SortDescriptions.Clear();
        string prop = (e.Column as DataGridBoundColumn)?.Binding is Binding b ? b.Path.Path : "";
        view.SortDescriptions.Add(new SortDescription(prop, dir));
        if (view is ListCollectionView lcv)
            lcv.CustomSort = new NaturalSortByProperty(prop, dir);
    }

    private void SetStatus(string msg)
    {
        if (Window.GetWindow(this) is MainWindow mw) mw.SetStatus(msg);
    }
}
