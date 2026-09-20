using System.Windows;
using System.Windows.Controls;
using WinNetManager.Models;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class FirewallRuleEditWindow : Window
{
    public FirewallRuleInfo Rule { get; private set; }
    private readonly bool _isEdit;

    public FirewallRuleEditWindow(FirewallRuleInfo? ruleToEdit, IEnumerable<string> interfaces)
    {
        InitializeComponent();
        _isEdit = ruleToEdit != null;
        Title = _isEdit ? "修改防火墙规则" : "新建防火墙规则";
        Rule = ruleToEdit?.Clone() ?? new FirewallRuleInfo
        {
            DisplayName = "WinNetManager_Inbound_",
            Direction = "Inbound",
            Action = "Allow",
            Profile = "Any",
            Protocol = "TCP",
            Enabled = "True",
        };

        SelectTag(CmbDirection, Rule.Direction, "Inbound");
        SelectTag(CmbAction, Rule.Action, "Allow");
        SelectTag(CmbProtocol, Rule.Protocol, "TCP");
        SelectTag(CmbProfile, string.IsNullOrWhiteSpace(Rule.Profile) ? "Any" : Rule.Profile, "Any");

        TxtDisplayName.Text = Rule.DisplayName;
        TxtLocalPort.Text = string.IsNullOrWhiteSpace(Rule.LocalPort) ? "" : Rule.LocalPort;
        TxtLocalAddress.Text = Rule.LocalAddress;

        CmbInterface.Items.Clear();
        CmbInterface.Items.Add("");
        foreach (var alias in interfaces.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a))
            CmbInterface.Items.Add(alias);

        if (!string.IsNullOrWhiteSpace(Rule.InterfaceAlias))
        {
            var match = CmbInterface.Items.Cast<object>().FirstOrDefault(i => string.Equals(i?.ToString(), Rule.InterfaceAlias, StringComparison.OrdinalIgnoreCase));
            if (match != null) CmbInterface.SelectedItem = match;
            else CmbInterface.Text = Rule.InterfaceAlias;
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        Rule.DisplayName = TxtDisplayName.Text?.Trim() ?? "";
        Rule.Direction = (CmbDirection.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Inbound";
        Rule.Action = (CmbAction.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Allow";
        Rule.Protocol = (CmbProtocol.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "TCP";
        Rule.Profile = (CmbProfile.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Any";
        Rule.LocalPort = TxtLocalPort.Text?.Trim() ?? "";
        Rule.LocalAddress = TxtLocalAddress.Text?.Trim() ?? "";
        Rule.InterfaceAlias = (CmbInterface.SelectedItem?.ToString() ?? CmbInterface.Text ?? "").Trim();

        if (!FirewallManager.ValidateRule(Rule, out string error))
        {
            CopyableMessageBox.Show(error, "输入无效", MessageBoxImage.Warning, this);
            return;
        }

        DialogResult = true;
        Close();
    }

    private static void SelectTag(ComboBox combo, string? value, string fallback)
    {
        string target = string.IsNullOrWhiteSpace(value) ? fallback : value;
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                item.IsSelected = true;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }
}
