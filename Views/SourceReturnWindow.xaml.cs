using System.Text;
using System.Windows;
using System.Windows.Controls;
using WinNetManager.Core.Net;
using WinNetManager.Models;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class SourceReturnWindow : Window
{
    private readonly SourceReturnService _service = new();
    private List<NetInterface> _interfaces = new();
    private List<ExistingDefaultRoute> _existing = new();
    private List<RouteEntry> _persistent = new();
    private List<NetworkAdapterInfo> _adapters = new();
    public bool Applied { get; private set; }

    public SourceReturnWindow(string? preferredAlias = null)
    {
        InitializeComponent();
        Loaded += (_, _) => LoadData(preferredAlias);
    }

    private void LoadData(string? preferredAlias)
    {
        try
        {
            _interfaces = _service.GetInterfaces();
            _existing = _service.GetExistingDefaults();
            _persistent = _service.GetPersistentRoutes();
            _adapters = _service.GetAdapters();
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show("加载网卡/路由失败：" + ex.Message, "错误", MessageBoxImage.Error, this);
            return;
        }

        var aliases = _interfaces.Select(i => i.InterfaceAlias)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a)
            .ToList();

        CmbInterface.Items.Clear();
        foreach (var alias in aliases)
            CmbInterface.Items.Add(alias);

        string? pick = preferredAlias;
        if (string.IsNullOrWhiteSpace(pick))
            pick = _adapters.FirstOrDefault(a => (a.IPv4Address ?? "").Contains("192.168.189."))?.Name;
        if (string.IsNullOrWhiteSpace(pick) && aliases.Count > 0)
            pick = aliases[0];

        if (!string.IsNullOrWhiteSpace(pick))
            CmbInterface.SelectedItem = aliases.FirstOrDefault(a => a.Equals(pick, StringComparison.OrdinalIgnoreCase)) ?? pick;

        TxtFirewallPorts.Text = "5555,8443,9443,11443,44301-44399,21114-21119";
        PrefillFromSelection();
        RefreshPreview();
    }

    private void CmbInterface_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        PrefillFromSelection();
        RefreshPreview();
    }

    private void ChkFirewall_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        TxtFirewallPorts.IsEnabled = ChkFirewall.IsChecked == true;
        RefreshPreview();
    }

    private void PrefillFromSelection()
    {
        string alias = SelectedAlias();
        if (string.IsNullOrWhiteSpace(alias)) return;

        var adapter = _adapters.FirstOrDefault(a => a.Name.Equals(alias, StringComparison.OrdinalIgnoreCase));
        string? v4 = null;
        var existingV4 = _persistent.FirstOrDefault(r =>
            r.InterfaceAlias.Equals(alias, StringComparison.OrdinalIgnoreCase)
            && r.AddressFamily == "IPv4"
            && !string.IsNullOrWhiteSpace(r.NextHop)
            && r.NextHop != "0.0.0.0");
        if (existingV4 != null)
            v4 = existingV4.NextHop;
        else if (!string.IsNullOrWhiteSpace(adapter?.Gateway))
            v4 = adapter!.Gateway.Split(',')[0].Trim();
        else
            v4 = SourceReturnService.GuessIpv4Gateway(adapter?.IPv4Address);

        if (!string.IsNullOrWhiteSpace(v4))
            TxtIpv4Hop.Text = v4;

        var existingV6 = _persistent.FirstOrDefault(r =>
            r.InterfaceAlias.Equals(alias, StringComparison.OrdinalIgnoreCase)
            && r.AddressFamily == "IPv6"
            && !string.IsNullOrWhiteSpace(r.NextHop)
            && r.NextHop != "::");
        if (existingV6 != null)
            TxtIpv6Hop.Text = existingV6.NextHop;
    }

    private void BtnPreview_Click(object sender, RoutedEventArgs e) => RefreshPreview(showError: true);

    private async void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildPlan(out var actions, out string error, showError: true))
            return;

        var others = _existing.Where(r =>
            !r.InterfaceAlias.Equals(SelectedAlias(), StringComparison.OrdinalIgnoreCase)
            && SourceReturnPlanner.IsDefaultPrefix(r.DestinationPrefix, r.AddressFamily)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("即将只对选中网卡写入以下变更：");
        sb.AppendLine();
        foreach (var a in actions)
            sb.AppendLine("  - " + Describe(a));
        sb.AppendLine();
        if (others.Count > 0)
        {
            sb.AppendLine("以下其他网卡的默认路由会保留不动：");
            foreach (var o in others)
                sb.AppendLine($"    {o.AddressFamily} {o.DestinationPrefix} via {o.NextHop} ({o.InterfaceAlias}, metric {o.RouteMetric})");
            sb.AppendLine();
        }
        sb.AppendLine("确定写入系统吗？");

        if (MessageBox.Show(sb.ToString(), "确认回源默认路由", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        BtnApply.IsEnabled = false;
        var (oks, errors) = await Task.Run(() => _service.Apply(actions));
        BtnApply.IsEnabled = true;

        string preview = SourceReturnService.BuildPreview(actions);
        if (Owner is MainWindow mw && !string.IsNullOrWhiteSpace(preview))
            mw.SetCommandPreview(preview);

        var result = new StringBuilder();
        if (oks.Count > 0)
        {
            result.AppendLine("成功 " + oks.Count + "  项：");
            foreach (var s in oks) result.AppendLine("  " + s);
            result.AppendLine();
        }
        if (errors.Count > 0)
        {
            result.AppendLine("失败 " + errors.Count + "  项：");
            foreach (var s in errors) result.AppendLine("  " + s);
        }

        CopyableMessageBox.Show(result.ToString(), errors.Count > 0 ? "操作结果" : "完成",
            errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information, this);

        if (oks.Count > 0)
        {
            Applied = true;
            DialogResult = true;
            Close();
        }
    }

    private void RefreshPreview(bool showError = false)
    {
        if (!TryBuildPlan(out var actions, out string error, showError))
        {
            TxtPreview.Text = error;
            return;
        }
        TxtPreview.Text = actions.Count == 0 ? "没有需要写入的变更（目标网卡已符合回源要求）。" : SourceReturnService.BuildPreview(actions);
    }

    private bool TryBuildPlan(out List<SourceReturnAction> actions, out string error, bool showError)
    {
        actions = new List<SourceReturnAction>();
        error = "";
        var req = new SourceReturnRequest
        {
            InterfaceAlias = SelectedAlias(),
            Ipv4NextHop = TxtIpv4Hop.Text,
            Ipv6NextHop = TxtIpv6Hop.Text,
            Metric = 500,
            SetStrongHost = ChkStrongHost.IsChecked == true,
            FirewallPorts = ChkFirewall.IsChecked == true ? TxtFirewallPorts.Text : null,
        };
        if (!int.TryParse(TxtMetric.Text?.Trim(), out int metric))
        {
            error = "跃点必须是 0 到 999999 之间的整数。";
            if (showError) CopyableMessageBox.Show(error, "输入无效", MessageBoxImage.Warning, this);
            return false;
        }
        req.Metric = metric;

        if (!SourceReturnPlanner.TryPlan(req, _existing, out actions, out error))
        {
            if (showError) CopyableMessageBox.Show(error, "输入无效", MessageBoxImage.Warning, this);
            return false;
        }
        return true;
    }

    private string SelectedAlias()
    {
        if (CmbInterface.SelectedItem is string s) return s;
        return CmbInterface.Text?.Trim() ?? "";
    }

    private static string Describe(SourceReturnAction a) => a.Kind switch
    {
        "AddRoute" => "新增默认路由 " + $"{a.AddressFamily} {a.DestinationPrefix} via {a.NextHop} metric {a.Metric}",
        "DeleteRoute" => "删除本接口旧默认路由 " + $"{a.AddressFamily} {a.DestinationPrefix} via {a.NextHop}",
        "ReplaceRoute" => "更新本接口默认路由跃点 " + $"{a.AddressFamily} {a.DestinationPrefix} via {a.NextHop} metric {a.Metric}",
        "SetStrongHost" => "设为强主机 " + a.InterfaceAlias,
        "AddFirewall" => "入站允许 TCP " + (a.Detail ?? ""),
        _ => a.Kind
    };
}
