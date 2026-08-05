using System.ComponentModel;

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
    private ChangeStatus _status = ChangeStatus.Unchanged;

    public string AddressFamily
    {
        get => _addressFamily;
        set { _addressFamily = value; OnPropertyChanged(nameof(AddressFamily)); }
    }

    public string DestinationPrefix
    {
        get => _destinationPrefix;
        set
        {
            _destinationPrefix = value;
            OnPropertyChanged(nameof(DestinationPrefix));
            OnPropertyChanged(nameof(DestinationDisplay));
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
        }
    }

    public string InterfaceAlias
    {
        get => _interfaceAlias;
        set { _interfaceAlias = value; OnPropertyChanged(nameof(InterfaceAlias)); }
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

