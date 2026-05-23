using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinNetManager.Models;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class DnsTab : UserControl
{
    private readonly DnsNrptManager _manager = new();
    private readonly ObservableCollection<NrptRule> _rules = new();

    public DnsTab()
    {
        InitializeComponent();
        NrptGrid.ItemsSource = _rules;
        Loaded += (_, _) => RefreshData();
    }

    private void RefreshData()
    {
        try
        {
            _rules.Clear();
            foreach (var r in _manager.GetRules())
                _rules.Add(r);
            SetStatus($"已加载 {_rules.Count} 条 NRPT 规则");
            EmptyState.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载 NRPT 规则失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private List<NrptRule> GetSelected() =>
        NrptGrid.SelectedItems.Cast<NrptRule>().ToList();

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshData();
    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => NrptGrid.SelectAll();
    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.InvertSelection(NrptGrid, _rules);

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var ns = NetworkProfileTab.PromptInput("新建 NRPT 规则",
            "域名后缀（如 *.corp.local 或 corp.local）：", "*.corp.local");
        if (string.IsNullOrEmpty(ns)) return;

        var servers = NetworkProfileTab.PromptInput("新建 NRPT 规则",
            "DNS 服务器地址（多个用逗号分隔）：", "10.0.0.1");
        if (string.IsNullOrEmpty(servers)) return;

        var comment = NetworkProfileTab.PromptInput("新建 NRPT 规则",
            "备注（可选）：", "");

        var res = _manager.AddRule(ns, servers, comment);
        if (res.Success)
        {
            SetStatus($"已添加 NRPT 规则：{ns} -> {servers}");
            RefreshData();
        }
        else
        {
            MessageBox.Show($"添加失败：{res.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择要删除的规则。", "未选择", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var names = string.Join("\n", selected.Select(r => $"  • {r.Namespace} -> {r.NameServers}"));
        if (MessageBox.Show($"确定要删除以下 {selected.Count} 条 NRPT 规则？\n\n{names}",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        int ok = 0;
        var errors = new List<string>();
        foreach (var rule in selected)
        {
            var res = _manager.DeleteRule(rule.Name, rule.GpoName);
            if (res.Success) ok++;
            else errors.Add($"{rule.Namespace}: {res.Message}");
        }

        if (errors.Count > 0)
        {
            MessageBox.Show($"成功删除 {ok}/{selected.Count} 条。\n\n失败项：\n{string.Join("\n", errors)}",
                "操作结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            SetStatus($"已删除 {ok}/{selected.Count} 条 NRPT 规则");
        }

        RefreshData();
    }

    private void BtnResolve_Click(object sender, RoutedEventArgs e)
    {
        string domain = TxtResolveDomain.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(domain))
        {
            MessageBox.Show("请输入要解析的域名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TxtResolveResult.Text = $"正在解析 {domain}...";

        try
        {
            var res = _manager.ResolveDomain(domain);
            if (res.Success)
            {
                TxtResolveResult.Text = res.Records.Count > 0
                    ? string.Join("\n", res.Records)
                    : "未解析到记录。";
                SetStatus($"{domain}：{res.Message}");
            }
            else
            {
                TxtResolveResult.Text = $"解析失败：{res.Message}";
                SetStatus($"解析 {domain} 失败");
            }
        }
        catch (Exception ex)
        {
            TxtResolveResult.Text = $"错误：{ex.Message}";
        }
    }

    private void BtnFlushDns_Click(object sender, RoutedEventArgs e)
    {
        var res = _manager.FlushCache();
        if (res.Success)
        {
            TbkFlushResult.Text = "DNS 缓存已刷新。";
            TbkFlushResult.Foreground = System.Windows.Media.Brushes.Green;
            SetStatus("DNS 缓存已刷新");
        }
        else
        {
            TbkFlushResult.Text = $"刷新失败：{res.Message}";
            TbkFlushResult.Foreground = System.Windows.Media.Brushes.Red;
        }
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e) =>
        NetworkProfileTab.CopySelectedCellValue(NrptGrid);

    private void MenuCopyAllResolve_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtResolveResult.Text))
            Clipboard.SetText(TxtResolveResult.Text);
    }

    private void MenuCopySelectedResolve_Click(object sender, RoutedEventArgs e)
    {
        string text = !string.IsNullOrEmpty(TxtResolveResult.SelectedText)
            ? TxtResolveResult.SelectedText
            : TxtResolveResult.Text;
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    private void SetStatus(string msg)
    {
        if (Window.GetWindow(this) is MainWindow mw) mw.SetStatus(msg);
    }
}
