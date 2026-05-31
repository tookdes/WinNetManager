using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class NetworkHealthWindow : Window
{
    private readonly List<NetworkHealthItem> _items;
    private ICollectionView? _view;

    public NetworkHealthWindow(List<NetworkHealthItem> items)
    {
        InitializeComponent();
        _items = items;

        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = ItemFilter;
        ResultGrid.ItemsSource = _view;

        int danger = _items.Count(i => i.Risk == "Danger");
        int warn = _items.Count(i => i.Risk == "Warn");
        int info = _items.Count(i => i.Risk == "Info");
        TbkSummary.Text = $"共 {_items.Count} 项  |  危险 {danger}  |  警告 {warn}  |  提示 {info}";
    }

    private bool ItemFilter(object obj)
    {
        if (obj is not NetworkHealthItem item) return false;

        var selectedRisk = (CmbRisk.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (!string.IsNullOrEmpty(selectedRisk) && item.Risk != selectedRisk)
            return false;

        string filter = TxtFilter.Text?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(filter)) return true;

        return (item.Category?.ToLowerInvariant().Contains(filter) == true)
            || (item.Name?.ToLowerInvariant().Contains(filter) == true)
            || (item.Status?.ToLowerInvariant().Contains(filter) == true)
            || (item.Detail?.ToLowerInvariant().Contains(filter) == true);
    }

    private void CmbRisk_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _view?.Refresh();
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _view?.Refresh();
    }

    private void BtnClearFilter_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        TxtFilter.Clear();
        CmbRisk.SelectedIndex = 0;
        _view?.Refresh();
    }

    private void BtnCopyAll_Click(object sender, RoutedEventArgs e)
    {
        var visible = _view?.Cast<NetworkHealthItem>().ToList() ?? _items;
        var lines = new List<string> { "类别\t项目\t状态\t风险\t详情" };
        lines.AddRange(visible.Select(i => $"{i.Category}\t{i.Name}\t{i.Status}\t{i.RiskDisplay}\t{i.Detail}"));
        try { Clipboard.SetText(string.Join("\n", lines)); } catch { }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
