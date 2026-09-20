namespace WinNetManager.Models;

public class FirewallRuleInfo
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Direction { get; set; } = "Inbound";
    public string Action { get; set; } = "Allow";
    public string Enabled { get; set; } = "True";
    public string Profile { get; set; } = "Any";
    public string Protocol { get; set; } = "TCP";
    public string LocalPort { get; set; } = "";
    public string InterfaceAlias { get; set; } = "";
    public string LocalAddress { get; set; } = "";
    public string EnabledDisplay => IsEnabled ? "是" : "否";
    public bool IsEnabled =>
        Enabled.Equals("True", StringComparison.OrdinalIgnoreCase)
        || Enabled.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
        || Enabled == "1";
    public bool IsWinNetManagerRule =>
        DisplayName.StartsWith("WinNetManager_", StringComparison.OrdinalIgnoreCase)
        || Name.StartsWith("WinNetManager_", StringComparison.OrdinalIgnoreCase);

    public FirewallRuleInfo Clone()
    {
        return new FirewallRuleInfo
        {
            Name = Name,
            DisplayName = DisplayName,
            Direction = Direction,
            Action = Action,
            Enabled = Enabled,
            Profile = Profile,
            Protocol = Protocol,
            LocalPort = LocalPort,
            InterfaceAlias = InterfaceAlias,
            LocalAddress = LocalAddress,
        };
    }
}
