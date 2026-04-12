using Microsoft.Win32;
using WinNetManager.Models;

namespace WinNetManager.Services;

public static class DeviceDescriptionService
{
    private const string DescriptionsKeyPath = @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\Descriptions";

    public static List<DeviceDescription> GetAllDescriptions()
    {
        var descriptions = new List<DeviceDescription>();
        using var key = Registry.LocalMachine.OpenSubKey(DescriptionsKeyPath);
        if (key == null) return descriptions;

        foreach (string valueName in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(valueName)) continue;

            string[] instanceNums = ReadMultiStringValue(key, valueName);

            descriptions.Add(new DeviceDescription
            {
                Name = valueName,
                InstanceNumbers = instanceNums
            });
        }

        return descriptions;
    }

    public static void ResetCounter(string descriptionName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(DescriptionsKeyPath, writable: true);
        if (key == null) return;

        // Reset to single instance ["1"]
        key.SetValue(descriptionName, new[] { "1" }, RegistryValueKind.MultiString);
    }

    public static void DeleteDescription(string descriptionName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(DescriptionsKeyPath, writable: true);
        key?.DeleteValue(descriptionName, throwOnMissingValue: false);
    }

    private static string[] ReadMultiStringValue(RegistryKey key, string valueName)
    {
        object? val = key.GetValue(valueName);
        if (val == null) return [];

        // REG_MULTI_SZ → string[]
        if (val is string[] arr)
            return arr.Where(s => !string.IsNullOrEmpty(s)).ToArray();

        // REG_SZ → single string, might contain space-separated numbers
        if (val is string str)
        {
            str = str.Trim();
            if (string.IsNullOrEmpty(str)) return [];
            return [str];
        }

        // REG_DWORD → single number
        if (val is int intVal)
            return intVal > 0 ? [intVal.ToString()] : [];

        return [];
    }
}
