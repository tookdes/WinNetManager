namespace WinNetManager.Models;

public class DeviceDescription
{
    public string Name { get; set; } = "";
    public string[] InstanceNumbers { get; set; } = [];

    public int Count => InstanceNumbers.Length;
    public int MaxInstance => InstanceNumbers.Length > 0
        ? InstanceNumbers.Select(s => int.TryParse(s, out int v) ? v : 0).Max()
        : 0;

    public string InstanceCountDisplay
    {
        get
        {
            if (InstanceNumbers.Length == 0) return "无数据";
            string nums = string.Join(", ", InstanceNumbers.Select(n => $"#{n}"));
            if (InstanceNumbers.Length == 1 && InstanceNumbers[0] == "1")
                return $"1 个实例 (无后缀)";
            return $"{InstanceNumbers.Length} 个实例 ({nums})";
        }
    }
}
