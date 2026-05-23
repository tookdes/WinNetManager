using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using WinNetManager.Models;
using WinNetManager.Services;

namespace WinNetManager.Views;

public partial class RouteEditWindow : Window
{
    public RouteEntry Route { get; private set; }
    private readonly List<NetInterface> _allInterfaces;
    private readonly bool _isEdit;

    public RouteEditWindow(RouteEntry? routeToEdit, List<NetInterface> interfaces)
    {
        InitializeComponent();
        _allInterfaces = interfaces ?? new List<NetInterface>();
        _isEdit = routeToEdit != null;

        if (_isEdit)
        {
            this.Title = "修改路由";
            Route = routeToEdit!.Clone();
        }
        else
        {
            this.Title = "新建路由";
            Route = new RouteEntry { RouteMetric = "1", AddressFamily = "IPv4" };
        }

        foreach (ComboBoxItem item in CmbAddressFamily.Items)
        {
            if (item.Tag?.ToString() == Route.AddressFamily)
            {
                item.IsSelected = true;
                break;
            }
        }

        RefreshInterfaceList();
        RefreshPrefixLengthItems();
        SyncPrefixFromRoute();
        TxtNextHop.Text = Route.NextHop;
        TxtMetric.Text = Route.RouteMetric;

        if (!string.IsNullOrEmpty(Route.InterfaceAlias))
        {
            var existing = CmbInterface.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Content?.ToString() == Route.InterfaceAlias);
            if (existing != null)
                CmbInterface.SelectedItem = existing;
            else
                CmbInterface.Text = Route.InterfaceAlias;
        }
    }

    private void CmbAddressFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshInterfaceList();
        RefreshPrefixLengthItems();
    }

    private void RefreshInterfaceList()
    {
        string family = (CmbAddressFamily.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "IPv4";
        CmbInterface.Items.Clear();

        var filtered = _allInterfaces
            .Where(i => i.AddressFamily == family)
            .OrderBy(i => i.InterfaceAlias)
            .ToList();

        foreach (var ni in filtered)
        {
            CmbInterface.Items.Add(new ComboBoxItem
            {
                Content = $"{ni.InterfaceAlias} (索引: {ni.InterfaceIndex})",
                Tag = ni.InterfaceAlias
            });
        }

        if (!string.IsNullOrEmpty(Route.InterfaceAlias))
        {
            var existing = CmbInterface.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == Route.InterfaceAlias);
            if (existing != null)
                CmbInterface.SelectedItem = existing;
            else
                CmbInterface.Text = Route.InterfaceAlias;
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        string family = (CmbAddressFamily.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "IPv4";
        string prefix = TxtDestinationPrefix.Text?.Trim() ?? "";
        string nextHop = TxtNextHop.Text?.Trim() ?? "";
        string metricText = TxtMetric.Text?.Trim() ?? "";
        string interfaceAlias = "";

        if (CmbInterface.SelectedItem is ComboBoxItem selectedItem)
            interfaceAlias = selectedItem.Tag?.ToString() ?? selectedItem.Content?.ToString() ?? "";
        else
            interfaceAlias = CmbInterface.Text?.Trim() ?? "";

        // 智能补全目标前缀掩码
        bool wasBareIp = !string.IsNullOrEmpty(prefix) && !prefix.Contains('/');
        if (wasBareIp)
        {
            string? completed = AutoCompletePrefix(prefix, family);
            if (completed != null)
            {
                prefix = completed;
                TxtDestinationPrefix.Text = prefix;
            }
        }

        if (string.IsNullOrEmpty(prefix))
        {
            MessageBox.Show(this, "请输入目标前缀。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(interfaceAlias))
        {
            MessageBox.Show(this, "请输入或选择接口别名。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(metricText, out int metric) || metric < 0)
        {
            MessageBox.Show(this, "度量值必须为正整数。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!IsValidDestinationPrefix(prefix, family))
        {
            if (wasBareIp)
            {
                MessageBox.Show(this, $"无法将 \"{prefix.Substring(0, prefix.IndexOf('/') >= 0 ? prefix.IndexOf('/') : prefix.Length)}\" 识别为有效的 {family} 地址。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (family == "IPv4")
            {
                MessageBox.Show(this, "目标前缀格式无效。\n\nIPv4 请使用 CIDR 格式，例如：192.168.1.0/24", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(this, "目标前缀格式无效。\n\nIPv6 请使用 CIDR 格式，例如：2400::/48", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        if (family == "IPv4")
        {
            if (!IPAddress.TryParse(nextHop, out IPAddress? ip) || ip?.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                MessageBox.Show(this, "下一跳必须是有效的 IPv4 地址。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            if (!IPAddress.TryParse(nextHop, out IPAddress? ip) || ip?.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                MessageBox.Show(this, "下一跳必须是有效的 IPv6 地址。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        Route.AddressFamily = family;
        Route.DestinationPrefix = prefix;
        Route.NextHop = nextHop;
        Route.InterfaceAlias = interfaceAlias;
        Route.RouteMetric = metricText;

        this.DialogResult = true;
        this.Close();
    }

    private bool IsValidDestinationPrefix(string prefix, string family)
    {
        if (string.IsNullOrEmpty(prefix)) return false;

        int slashIndex = prefix.LastIndexOf('/');
        if (slashIndex < 0) return false;

        string addressPart = prefix.Substring(0, slashIndex);
        string prefixPart = prefix.Substring(slashIndex + 1);

        if (!IPAddress.TryParse(addressPart, out IPAddress? ip))
            return false;

        if (!int.TryParse(prefixPart, out int prefixLength))
            return false;

        if (family == "IPv4")
        {
            if (ip?.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            if (prefixLength < 0 || prefixLength > 32) return false;
        }
        else
        {
            if (ip?.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
            if (prefixLength < 0 || prefixLength > 128) return false;
        }

        return true;
    }

    /// <summary>
    /// 自动补全目标前缀的 CIDR 掩码。
    /// 纯 IPv4 地址默认补 /32，纯 IPv6 地址默认补 /128。
    /// 高级用户可手动输入 /xx 覆盖。
    /// </summary>
    private static string? AutoCompletePrefix(string input, string family)
    {
        if (input.Contains('/')) return null;

        if (family == "IPv4" && IPAddress.TryParse(input, out IPAddress? ip4)
            && ip4?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return $"{ip4}/32";
        }

        if (family == "IPv6" && IPAddress.TryParse(input, out IPAddress? ip6)
            && ip6?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return $"{ip6}/128";
        }

        return null;
    }

    // --- 掩码下拉框 ---

    private void RefreshPrefixLengthItems()
    {
        string family = (CmbAddressFamily.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "IPv4";
        CmbPrefixLength.Items.Clear();
        CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "请选择覆盖范围", Tag = "", IsSelected = true });

        if (family == "IPv4")
        {
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/0   全部地址", Tag = "0" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/1   半个IPv4(21亿)", Tag = "1" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/2   64个A类网", Tag = "2" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/3   32个A类网", Tag = "3" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/4   16个A类网", Tag = "4" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/5   8个A类网", Tag = "5" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/6   4个A类网", Tag = "6" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/7   2个A类网", Tag = "7" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/8   1个A类网(1600万)", Tag = "8" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/9   半个A类网", Tag = "9" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/10  四分之一A类", Tag = "10" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/11  八分之一A类", Tag = "11" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/12  十六分之一A类", Tag = "12" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/13  三十二分之一A类", Tag = "13" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/14  六十四分之一A类", Tag = "14" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/15  2个B类网", Tag = "15" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/16  1个B类网(6.5万)", Tag = "16" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/17  半个B类网", Tag = "17" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/18  四分之一B类", Tag = "18" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/19  八分之一B类", Tag = "19" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/20  十六分之一B类", Tag = "20" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/21  三十二分之一B类", Tag = "21" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/22  六十四分之一B类", Tag = "22" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/23  2个C类网", Tag = "23" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/24  1个C类网(254台)", Tag = "24" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/25  半C类(126台)", Tag = "25" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/26  四分之一C类(62台)", Tag = "26" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/27  八分之一C类(30台)", Tag = "27" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/28  十六分之一C类(14台)", Tag = "28" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/29  三十二分之一C类(6台)", Tag = "29" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/30  点对点链路(2台)", Tag = "30" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/31  点对点链路", Tag = "31" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/32  单台主机", Tag = "32" });
        }
        else
        {
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/0    全部地址", Tag = "0" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/16   超大规模", Tag = "16" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/32   运营商大段", Tag = "32" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/36   大型网络", Tag = "36" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/40   地区网络", Tag = "40" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/44   机构网络", Tag = "44" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/48   常见前缀", Tag = "48" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/52   中型网络", Tag = "52" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/56   小型网络", Tag = "56" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/60   超小型网络", Tag = "60" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/62   微网络", Tag = "62" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/64   SLAAC标准", Tag = "64" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/96   兼容IPv4", Tag = "96" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/112  单段子网", Tag = "112" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/120  256个地址", Tag = "120" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/124  16个地址", Tag = "124" });
            CmbPrefixLength.Items.Add(new ComboBoxItem { Content = "/128  单个地址", Tag = "128" });
        }
    }

    private void SyncPrefixFromRoute()
    {
        string raw = Route.DestinationPrefix;
        if (string.IsNullOrEmpty(raw))
        {
            TxtDestinationPrefix.Text = "";
            CmbPrefixLength.SelectedIndex = 0;
            return;
        }

        int slash = raw.LastIndexOf('/');
        if (slash >= 0)
        {
            TxtDestinationPrefix.Text = raw.Substring(0, slash).Trim();
            string len = raw.Substring(slash + 1).Trim();
            var match = CmbPrefixLength.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == len);
            if (match != null)
                CmbPrefixLength.SelectedItem = match;
            else
                CmbPrefixLength.SelectedIndex = 0;
        }
        else
        {
            TxtDestinationPrefix.Text = raw;
            CmbPrefixLength.SelectedIndex = 0;
        }
    }

    private void CmbPrefixLength_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var selected = CmbPrefixLength.SelectedItem as ComboBoxItem;
        if (selected == null || string.IsNullOrEmpty(selected.Tag?.ToString())) return;

        string len = selected.Tag.ToString()!;
        string ip = TxtDestinationPrefix.Text?.Trim() ?? "";

        // 如果当前文本已包含 /，先去掉旧掩码
        int slash = ip.LastIndexOf('/');
        if (slash >= 0)
            ip = ip.Substring(0, slash).Trim();

        if (!string.IsNullOrEmpty(ip))
            TxtDestinationPrefix.Text = $"{ip}/{len}";
    }

    // --- 实时 CIDR 提示 ---

    private void TxtDestinationPrefix_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = TxtDestinationPrefix.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text))
        {
            TbkPrefixInfo.Text = "";
            return;
        }

        string family = (CmbAddressFamily.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "IPv4";
        string? info = family == "IPv4" ? FormatIpv4CidrInfo(text) : FormatIpv6CidrInfo(text);
        TbkPrefixInfo.Text = info ?? "";
    }

    private static string? FormatIpv4CidrInfo(string cidr)
    {
        int slash = cidr.LastIndexOf('/');
        if (slash < 0) return null;
        string addrStr = cidr.Substring(0, slash).Trim();
        if (!int.TryParse(cidr.Substring(slash + 1).Trim(), out int prefixLen) || prefixLen < 0 || prefixLen > 32)
            return null;
        if (!IPAddress.TryParse(addrStr, out IPAddress? ip) || ip?.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return null;

        byte[] bytes = ip.GetAddressBytes();
        uint ipUint = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        uint mask = prefixLen == 0 ? 0 : 0xFFFFFFFFu << (32 - prefixLen);
        uint network = ipUint & mask;
        uint broadcast = ipUint | ~mask;

        string netStr = UintToIp(network);
        string bcStr = UintToIp(broadcast);

        if (prefixLen == 32)
            return $"主机地址：{netStr}";
        if (prefixLen == 31)
            return $"链路地址：{netStr} — {bcStr}（2个地址，无广播）";
        if (prefixLen == 30)
            return $"子网：{netStr} — {bcStr}（可用 2 个主机地址）";

        uint first = network + 1;
        uint last = broadcast - 1;
        ulong count = (ulong)(broadcast - network - 1);
        return $"网络：{netStr}  可用主机：{UintToIp(first)} — {UintToIp(last)}  共约 {count:N0} 个";
    }

    private static string? FormatIpv6CidrInfo(string cidr)
    {
        int slash = cidr.LastIndexOf('/');
        if (slash < 0) return null;
        string addrStr = cidr.Substring(0, slash).Trim();
        if (!int.TryParse(cidr.Substring(slash + 1).Trim(), out int prefixLen) || prefixLen < 0 || prefixLen > 128)
            return null;
        if (!IPAddress.TryParse(addrStr, out IPAddress? ip) || ip?.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            return null;

        string desc = prefixLen switch
        {
            0 => "全部 IPv6 地址",
            32 => "运营商大段",
            48 => "常见站点前缀",
            56 => "小型站点",
            64 => "标准 SLAAC 子网",
            96 => "兼容 IPv4 映射",
            128 => "单个地址",
            _ => prefixLen < 64 ? "大型网络前缀" : "子网前缀"
        };
        return $"前缀：{addrStr}/{prefixLen}  （{desc}）";
    }

    private static string UintToIp(uint value)
    {
        return $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
    }

    // --- 范围计算器 ---

    private void BtnToggleCalc_Click(object sender, RoutedEventArgs e)
    {
        bool visible = GridCalc.Visibility == Visibility.Visible;
        GridCalc.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        TbkCalcResult.Text = "";
        BtnToggleCalc.Content = visible ? "不知道掩码？使用范围计算器" : "隐藏范围计算器";
    }

    private void BtnCalc_Click(object sender, RoutedEventArgs e)
    {
        string start = TxtCalcStart.Text?.Trim() ?? "";
        string end = TxtCalcEnd.Text?.Trim() ?? "";
        string family = (CmbAddressFamily.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "IPv4";

        string? result = CalculateMinCidr(start, end, family);
        if (result == null)
        {
            TbkCalcResult.Text = "无法计算。请确保起始和结束 IP 都是有效的 {family} 地址，且起始 ≤ 结束。";
            TbkCalcResult.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        TbkCalcResult.Text = $"覆盖范围的最小 CIDR：{result}";
        TbkCalcResult.Foreground = System.Windows.Media.Brushes.DarkGreen;

        // 自动填入目标前缀框
        TxtDestinationPrefix.Text = result;
        SyncPrefixLengthFromText(result);
    }

    private static string? CalculateMinCidr(string startStr, string endStr, string family)
    {
        if (family == "IPv4")
        {
            if (!IPAddress.TryParse(startStr, out IPAddress? startIp) || startIp?.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return null;
            if (!IPAddress.TryParse(endStr, out IPAddress? endIp) || endIp?.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return null;

            uint start = BytesToUint(startIp.GetAddressBytes());
            uint end = BytesToUint(endIp.GetAddressBytes());
            if (end < start) (start, end) = (end, start);

            // 找到覆盖两者的最小前缀长度
            uint xor = start ^ end;
            int prefixLen = 32;
            while (xor != 0) { xor >>= 1; prefixLen--; }

            uint mask = prefixLen == 0 ? 0 : 0xFFFFFFFFu << (32 - prefixLen);
            uint network = start & mask;
            return $"{UintToIp(network)}/{prefixLen}";
        }

        // IPv6 暂不实现范围计算器（地址空间过大，需求较少）
        return null;
    }

    private static uint BytesToUint(byte[] bytes)
    {
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private void SyncPrefixLengthFromText(string prefix)
    {
        int slash = prefix.LastIndexOf('/');
        if (slash >= 0)
        {
            string len = prefix.Substring(slash + 1).Trim();
            var match = CmbPrefixLength.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == len);
            if (match != null)
                CmbPrefixLength.SelectedItem = match;
            else
                CmbPrefixLength.SelectedIndex = 0;
        }
    }
}
