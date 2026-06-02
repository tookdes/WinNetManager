using System;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using WinNetManager.Models;

namespace WinNetManager.Views;

public partial class PortProxyEditWindow : Window
{
    public PortProxyRule Rule { get; private set; }
    private readonly bool _isEdit;

    public PortProxyEditWindow(PortProxyRule? ruleToEdit)
    {
        InitializeComponent();
        _isEdit = ruleToEdit != null;

        if (_isEdit)
        {
            this.Title = "修改端口转发";
            Rule = ruleToEdit!.Clone();
        }
        else
        {
            this.Title = "新建端口转发";
            Rule = new PortProxyRule { Direction = "v4tov4", Protocol = "tcp" };
        }

        foreach (ComboBoxItem item in CmbDirection.Items)
        {
            if (item.Tag?.ToString() == Rule.Direction)
            {
                item.IsSelected = true;
                break;
            }
        }

        TxtListenAddress.Text = Rule.ListenAddress;
        TxtListenPort.Text = Rule.ListenPort;
        TxtConnectAddress.Text = Rule.ConnectAddress;
        TxtConnectPort.Text = Rule.ConnectPort;
        TxtProtocol.Text = Rule.Protocol;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        string direction = (CmbDirection.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "v4tov4";
        string listenAddr = TxtListenAddress.Text?.Trim() ?? "";
        string listenPortText = TxtListenPort.Text?.Trim() ?? "";
        string connectAddr = TxtConnectAddress.Text?.Trim() ?? "";
        string connectPortText = TxtConnectPort.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(listenAddr) || !IsValidAddress(listenAddr))
        {
            CopyableMessageBox.Show("监听地址必须是有效的 IP 地址，且不能包含特殊字符。", "输入无效", MessageBoxImage.Warning, this);
            return;
        }

        if (!IsValidPort(listenPortText, out int listenPort))
        {
            CopyableMessageBox.Show("监听端口必须是 1-65535 之间的整数。", "输入无效", MessageBoxImage.Warning, this);
            return;
        }

        if (string.IsNullOrEmpty(connectAddr) || !IsValidAddress(connectAddr))
        {
            CopyableMessageBox.Show("目标地址必须是有效的 IP 地址，且不能包含特殊字符。", "输入无效", MessageBoxImage.Warning, this);
            return;
        }

        if (!IsValidPort(connectPortText, out int connectPort))
        {
            CopyableMessageBox.Show("目标端口必须是 1-65535 之间的整数。", "输入无效", MessageBoxImage.Warning, this);
            return;
        }

        if (IPAddress.TryParse(listenAddr, out IPAddress? listenIp)
            && IPAddress.TryParse(connectAddr, out IPAddress? connectIp)
            && listenIp?.Equals(connectIp) == true
            && listenPort == connectPort)
        {
            CopyableMessageBox.Show("监听地址和端口不能与目标地址和端口相同（防止循环转发）。", "输入无效", MessageBoxImage.Warning, this);
            return;
        }

        Rule.Direction = direction;
        Rule.ListenAddress = listenAddr;
        Rule.ListenPort = listenPortText;
        Rule.ConnectAddress = connectAddr;
        Rule.ConnectPort = connectPortText;
        Rule.Protocol = "tcp";

        this.DialogResult = true;
        this.Close();
    }

    private static bool IsValidAddress(string address)
    {
        if (string.IsNullOrEmpty(address)) return false;
        // Block characters that could be used for injection or break netsh
        if (address.IndexOfAny(new[] { '\"', ';', '&', '|', '<', '>', '(', ')' }) >= 0)
            return false;
        return System.Net.IPAddress.TryParse(address, out _);
    }

    private static bool IsValidPort(string text, out int port)
    {
        port = 0;
        return int.TryParse(text, out port) && port > 0 && port <= 65535;
    }
}
