using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using WinNetManager.Core.Models;

namespace WinNetManager.Views;

public partial class TemplatePickerWindow : Window
{
    private readonly IReadOnlyList<AdapterOption> _adapters;

    public TemplatePickerWindow(IReadOnlyList<AdapterOption> adapters)
    {
        InitializeComponent();
        _adapters = adapters;
        CmbAdapterA.ItemsSource = _adapters;
        CmbAdapterB.ItemsSource = _adapters;
        CmbAdapterC.ItemsSource = _adapters;
        if (_adapters.Count > 0) CmbAdapterA.SelectedIndex = 0;
        if (_adapters.Count > 1) CmbAdapterB.SelectedIndex = 1;
        if (_adapters.Count > 2) CmbAdapterC.SelectedIndex = 2;
        else if (_adapters.Count > 0) CmbAdapterC.SelectedIndex = 0;
        CmbTemplate.SelectedIndex = 0;
    }

    public AutomationRule? ResultRule { get; private set; }

    private void CmbTemplate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbTemplate.SelectedItem is not ComboBoxItem ci) return;
        string tag = (ci.Tag as string) ?? "";
        bool hasA = tag is "ipv6soft" or "ipv6restart" or "ipv4notify" or "fallback";
        bool hasB = tag is "ipv4notify" or "fallback";
        bool hasC = tag == "fallback";
        bool v6 = tag is "ipv6soft" or "ipv6restart";
        bool v4A = tag is "ipv4notify" or "fallback";
        bool v4B = tag == "fallback";
        bool url = tag is "ipv4notify" or "fallback";
        bool socks = tag == "fallback";
        bool names = tag == "mergeprofile";

        LblAdapterA.Visibility = hasA ? Visibility.Visible : Visibility.Collapsed;
        CmbAdapterA.Visibility = hasA ? Visibility.Visible : Visibility.Collapsed;
        LblAdapterB.Visibility = hasB ? Visibility.Visible : Visibility.Collapsed;
        CmbAdapterB.Visibility = hasB ? Visibility.Visible : Visibility.Collapsed;
        LblAdapterC.Visibility = hasC ? Visibility.Visible : Visibility.Collapsed;
        CmbAdapterC.Visibility = hasC ? Visibility.Visible : Visibility.Collapsed;
        LblTargetV6.Visibility = v6 ? Visibility.Visible : Visibility.Collapsed;
        TxtTargetV6.Visibility = v6 ? Visibility.Visible : Visibility.Collapsed;
        LblTargetV4A.Visibility = v4A ? Visibility.Visible : Visibility.Collapsed;
        TxtTargetV4A.Visibility = v4A ? Visibility.Visible : Visibility.Collapsed;
        LblTargetV4B.Visibility = v4B ? Visibility.Visible : Visibility.Collapsed;
        TxtTargetV4B.Visibility = v4B ? Visibility.Visible : Visibility.Collapsed;
        LblUrl.Visibility = url ? Visibility.Visible : Visibility.Collapsed;
        TxtUrl.Visibility = url ? Visibility.Visible : Visibility.Collapsed;
        LblSocks.Visibility = socks ? Visibility.Visible : Visibility.Collapsed;
        TxtSocks.Visibility = socks ? Visibility.Visible : Visibility.Collapsed;
        LblProfileNames.Visibility = names ? Visibility.Visible : Visibility.Collapsed;
        TxtKeepName.Visibility = names ? Visibility.Visible : Visibility.Collapsed;
        TxtDupName.Visibility = names ? Visibility.Visible : Visibility.Collapsed;
        PanelProfileNames.Visibility = names ? Visibility.Visible : Visibility.Collapsed;

        TbkHint.Text = tag switch
        {
            "ipv6soft" => "提示：检测到 IPv6 无法到达目标且持续 3 次后，先执行 ipconfig /renew6 软刷新（不物理断网）。配合「重启保底」规则使用。",
            "ipv6restart" => "提示：软刷新无效（连续 6 次仍失败）后物理重启网卡。重启后进入抑制窗口，避免误触发其他规则。",
            "ipv4notify" => "提示：A 的 IPv4 掉线后重启 A，等 30 秒经 B 的 IPv4 接口访问 URL。URL 建议用固定 IP 或 --resolve 避免 DNS 走默认路由。",
            "fallback" => "提示：A、B 同时不通（同一时刻快照）才触发；先经 C 的 IPv4 访问 URL，失败再走 SOCKS5。默认冷却 30 分钟、每天最多 4 次，保护 4G 流量。",
            "mergeprofile" => "提示：实验性。检测「网络 2」且其最后连接晚于「网络」时，备份后删除旧「网络」并把「网络 2」改名。默认永远 dry-run。",
            _ => "",
        };
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        ResultRule = null;
        DialogResult = false;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        string tag = ((CmbTemplate.SelectedItem as ComboBoxItem)?.Tag as string) ?? "";
        AutomationRule? rule = tag switch
        {
            "ipv6soft" => RuleTemplates.Ipv6SelfHealSoft(CmbAdapterA.SelectedValue as string ?? "", TxtTargetV6.Text.Trim()),
            "ipv6restart" => RuleTemplates.Ipv6SelfHealRestart(CmbAdapterA.SelectedValue as string ?? "", TxtTargetV6.Text.Trim()),
            "ipv4notify" => RuleTemplates.Ipv4RestartAndNotifyVia(
                CmbAdapterA.SelectedValue as string ?? "", CmbAdapterB.SelectedValue as string ?? "",
                TxtTargetV4A.Text.Trim(), TxtUrl.Text.Trim()),
            "fallback" => RuleTemplates.FallbackWhenAllDown(
                CmbAdapterA.SelectedValue as string ?? "", CmbAdapterB.SelectedValue as string ?? "",
                CmbAdapterC.SelectedValue as string ?? "", TxtTargetV4A.Text.Trim(), TxtTargetV4B.Text.Trim(),
                TxtUrl.Text.Trim(), TxtSocks.Text.Trim()),
            "mergeprofile" => RuleTemplates.MergeNetworkProfiles(TxtKeepName.Text.Trim(), TxtDupName.Text.Trim()),
            _ => null,
        };

        if (rule == null)
        {
            MessageBox.Show("请先选择模板。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // 模板必填参数校验：适配器未选或目标为空时给出明确提示
        bool needsAdapterA = tag is "ipv6soft" or "ipv6restart" or "ipv4notify" or "fallback";
        bool needsAdapterB = tag is "ipv4notify" or "fallback";
        bool needsAdapterC = tag == "fallback";
        bool needsTargetV6 = tag is "ipv6soft" or "ipv6restart";
        bool needsTargetV4 = tag is "ipv4notify" or "fallback";
        bool needsUrl = tag is "ipv4notify" or "fallback";
        bool needsNames = tag == "mergeprofile";

        if (needsAdapterA && string.IsNullOrWhiteSpace(CmbAdapterA.SelectedValue as string))
        {
            MessageBox.Show("请选择网卡 A。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (needsAdapterB && string.IsNullOrWhiteSpace(CmbAdapterB.SelectedValue as string))
        {
            MessageBox.Show("请选择网卡 B。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (needsAdapterC && string.IsNullOrWhiteSpace(CmbAdapterC.SelectedValue as string))
        {
            MessageBox.Show("请选择网卡 C（4G 兜底）。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (needsTargetV6 && string.IsNullOrWhiteSpace(TxtTargetV6.Text.Trim()))
        {
            MessageBox.Show("请填写 IPv6 目标地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (needsTargetV4 && string.IsNullOrWhiteSpace(TxtTargetV4A.Text.Trim()))
        {
            MessageBox.Show("请填写 IPv4 目标 A。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (needsUrl && string.IsNullOrWhiteSpace(TxtUrl.Text.Trim()))
        {
            MessageBox.Show("请填写通知 URL。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (needsNames && (string.IsNullOrWhiteSpace(TxtKeepName.Text.Trim()) || string.IsNullOrWhiteSpace(TxtDupName.Text.Trim())))
        {
            MessageBox.Show("请填写保留名与重复名（如 网络 / 网络 2）。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (rule.Condition.Children.Count == 0)
        {
            MessageBox.Show("请补全模板参数。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ResultRule = rule;
        DialogResult = true;
    }
}
