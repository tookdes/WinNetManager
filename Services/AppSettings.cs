using System;
using System.IO;
using System.Text.Json;

namespace WinNetManager.Services;

public class AppSettings
{
    public string Theme { get; set; } = "Light";

    // ---------- 自动化监控引擎 ----------
    /// <summary>自动化引擎全局总开关（默认关闭，需手动开启）。</summary>
    public bool AutomationGlobalEnabled { get; set; }

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinNetManager",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return new AppSettings();
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            string path = SettingsPath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 设置写入失败不影响主功能
        }
    }
}
