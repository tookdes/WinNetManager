using System;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace WinNetManager.Models;

public enum ChangeStatus
{
    Unchanged,
    Modified,
    Added,
    Deleted
}

public class RouteEntry : INotifyPropertyChanged
{
    private string _addressFamily = "";
    private string _destinationPrefix = "";
    private string _nextHop = "";
    private string _interfaceAlias = "";
    private string _interfaceIndex = "";
    private string _routeMetric = "";
    private string _store = "";
    private bool _isDynamicGateway;
    private string _dynamicCurrentGateway = "";
    private string _dynamicTaskStatus = "";
    private ChangeStatus _status = ChangeStatus.Unchanged;

    public string AddressFamily
    {
        get => _addressFamily;
        set
        {
            _addressFamily = value;
            OnPropertyChanged(nameof(AddressFamily));
            OnPropertyChanged(nameof(DynamicTriggerTaskName));
        }
    }

    public string DestinationPrefix
    {
        get => _destinationPrefix;
        set
        {
            _destinationPrefix = value;
            OnPropertyChanged(nameof(DestinationPrefix));
            OnPropertyChanged(nameof(DestinationDisplay));
            OnPropertyChanged(nameof(DynamicTriggerTaskName));
            OnPropertyChanged(nameof(DynamicGatewayToolTip));
        }
    }

    public string NextHop
    {
        get => _nextHop;
        set
        {
            _nextHop = value;
            OnPropertyChanged(nameof(NextHop));
            OnPropertyChanged(nameof(NextHopDisplay));
            OnPropertyChanged(nameof(DynamicGatewayToolTip));
        }
    }

    public string InterfaceAlias
    {
        get => _interfaceAlias;
        set
        {
            _interfaceAlias = value;
            OnPropertyChanged(nameof(InterfaceAlias));
            OnPropertyChanged(nameof(DynamicTriggerTaskName));
            OnPropertyChanged(nameof(DynamicGatewayToolTip));
        }
    }

    public string InterfaceIndex
    {
        get => _interfaceIndex;
        set { _interfaceIndex = value; OnPropertyChanged(nameof(InterfaceIndex)); }
    }

    public string RouteMetric
    {
        get => _routeMetric;
        set { _routeMetric = value; OnPropertyChanged(nameof(RouteMetric)); }
    }

    public string Store
    {
        get => _store;
        set { _store = value; OnPropertyChanged(nameof(Store)); }
    }

    /// <summary>
    /// True when this route is managed by a Windows scheduled task that follows
    /// the selected interface's current IPv4 default gateway.
    /// </summary>
    public bool IsDynamicGateway
    {
        get => _isDynamicGateway;
        set
        {
            _isDynamicGateway = value;
            OnPropertyChanged(nameof(IsDynamicGateway));
            OnPropertyChanged(nameof(NextHopDisplay));
            OnPropertyChanged(nameof(DynamicTriggerTaskName));
            OnPropertyChanged(nameof(DynamicGatewayToolTip));
        }
    }

    /// <summary>
    /// Current IPv4 default gateway observed on the selected interface.
    /// This is informational and is not serialized as the route definition.
    /// </summary>
    public string DynamicCurrentGateway
    {
        get => _dynamicCurrentGateway;
        set
        {
            _dynamicCurrentGateway = value ?? "";
            OnPropertyChanged(nameof(DynamicCurrentGateway));
            OnPropertyChanged(nameof(NextHopDisplay));
            OnPropertyChanged(nameof(DynamicGatewayToolTip));
        }
    }

    /// <summary>
    /// Scheduled-task registration state, e.g. "已注册/启用".
    /// </summary>
    public string DynamicTaskStatus
    {
        get => _dynamicTaskStatus;
        set
        {
            _dynamicTaskStatus = value ?? "";
            OnPropertyChanged(nameof(DynamicTaskStatus));
            OnPropertyChanged(nameof(DynamicGatewayToolTip));
        }
    }

    /// <summary>
    /// Deterministic, shell-safe task name. The hash is the identity; readable
    /// fragments are only there to help administrators recognize the task.
    /// </summary>
    public string DynamicTriggerTaskName
    {
        get
        {
            string identity = $"{AddressFamily}|{InterfaceAlias}|{DestinationPrefix}";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12];
            string alias = ToTaskNameFragment(InterfaceAlias, 24);
            string prefix = ToTaskNameFragment(DestinationPrefix, 28);
            return $"WinNetManager_DynamicRoute_{alias}_{prefix}_{hash}";
        }
    }

    public string DynamicGatewayToolTip
    {
        get
        {
            if (!IsDynamicGateway)
                return $"固定下一跳：{NextHop}";

            string current = string.IsNullOrWhiteSpace(DynamicCurrentGateway)
                ? "尚未获取"
                : DynamicCurrentGateway;
            string installed = string.IsNullOrWhiteSpace(NextHop)
                ? "尚未安装"
                : NextHop;
            string task = string.IsNullOrWhiteSpace(DynamicTaskStatus)
                ? "未知"
                : DynamicTaskStatus;

            return $"动态下一跳（跟随接口当前 IPv4 默认网关）\n" +
                   $"当前网关：{current}\n" +
                   $"已安装下一跳：{installed}\n" +
                   $"计划任务：{DynamicTriggerTaskName}\n" +
                   $"任务状态：{task}";
        }
    }

    private static string ToTaskNameFragment(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "NA";

        var sb = new StringBuilder();
        bool lastUnderscore = false;
        foreach (char ch in value)
        {
            bool safe = (ch >= 'A' && ch <= 'Z') ||
                        (ch >= 'a' && ch <= 'z') ||
                        (ch >= '0' && ch <= '9') ||
                        ch is '-' or '_';

            char mapped = safe ? ch : '_';
            if (mapped == '_')
            {
                if (lastUnderscore)
                    continue;
                lastUnderscore = true;
            }
            else
            {
                lastUnderscore = false;
            }

            sb.Append(mapped);
            if (sb.Length >= maxLength)
                break;
        }

        string result = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "X" : result;
    }

    public ChangeStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(nameof(Status)); }
    }

    public string DestinationDisplay
    {
        get
        {
            if (AddressFamily == "IPv4" && (DestinationPrefix == "0.0.0.0/0" || DestinationPrefix == "0.0.0.0"))
                return "default";
            if (AddressFamily == "IPv6" && (DestinationPrefix == "::/0" || DestinationPrefix == "::"))
                return "default";
            return DestinationPrefix;
        }
    }

    public string NextHopDisplay
    {
        get
        {
            if (IsDynamicGateway)
            {
                if (!string.IsNullOrWhiteSpace(DynamicCurrentGateway))
                {
                    if (!string.IsNullOrWhiteSpace(NextHop) &&
                        !string.Equals(NextHop, DynamicCurrentGateway, StringComparison.OrdinalIgnoreCase))
                    {
                        return $"[DHCP] 自动 (网关: {DynamicCurrentGateway}, 已装: {NextHop})";
                    }

                    return $"[DHCP] 自动 (当前: {DynamicCurrentGateway})";
                }

                return string.IsNullOrWhiteSpace(NextHop)
                    ? "[DHCP] 自动 (等待网关)"
                    : $"[DHCP] 自动 (当前不可用, 已装: {NextHop})";
            }

            if (AddressFamily == "IPv4" && (DestinationPrefix == "0.0.0.0/0" || DestinationPrefix == "0.0.0.0"))
                return $"{NextHop} (默认)";
            return NextHop;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public RouteEntry Clone()
    {
        return (RouteEntry)MemberwiseClone();
    }

}

