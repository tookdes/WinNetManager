using System.Windows;
using System.Windows.Controls;
using WinNetManager.Models;

namespace WinNetManager.Views;

public partial class IpConfigEditWindow : Window
{
    public string IpAddress => TxtIp.Text.Trim();
    public int PrefixLength { get; private set; }
    public string Gateway => TxtGateway.Text.Trim();

    public IpConfigEditWindow(NetworkAdapterInfo adapter)
    {
        InitializeComponent();
        TxtAdapter.Text = adapter.Name;

        // 预填当前值（多地址时取第一个）
        string ip = adapter.IPv4Address?.Split(',')[0].Trim() ?? "";
        TxtIp.Text = ip;
        TxtPrefix.Text = string.IsNullOrEmpty(adapter.PrefixLength) ? "24" : adapter.PrefixLength;
        TxtGateway.Text = adapter.Gateway?.Split(',')[0].Trim() ?? "";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (!System.Net.IPAddress.TryParse(IpAddress, out var addr) ||
            addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            CopyableMessageBox.Show("请输入有效的 IPv4 地址。", "输入无效", MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(TxtPrefix.Text.Trim(), out int prefix) || prefix < 0 || prefix > 32)
        {
            CopyableMessageBox.Show("前缀长度须为 0–32 的整数（例如 24 表示 /24）。", "输入无效", MessageBoxImage.Warning);
            return;
        }

        if (!string.IsNullOrEmpty(Gateway))
        {
            if (!System.Net.IPAddress.TryParse(Gateway, out var gw) ||
                gw.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                CopyableMessageBox.Show("网关地址无效。可留空表示不设置默认网关。", "输入无效", MessageBoxImage.Warning);
                return;
            }
        }

        PrefixLength = prefix;
        DialogResult = true;
    }
}
