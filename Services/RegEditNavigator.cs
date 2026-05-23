using System.Diagnostics;
using Microsoft.Win32;

namespace WinNetManager.Services;

public static class RegEditNavigator
{
    public static void OpenAt(string hiveAndPath)
    {
        // Detect the localized "Computer" prefix used by regedit on this system.
        // Chinese Windows uses "计算机", English uses "Computer", etc.
        string prefix = DetectPrefix();
        string fullPath = string.IsNullOrEmpty(prefix)
            ? hiveAndPath
            : $"{prefix}\\{hiveAndPath}";

        foreach (var proc in Process.GetProcessesByName("regedit"))
        {
            try
            {
                // 先尝试优雅关闭（允许用户保存），超时后再强制结束
                if (!proc.CloseMainWindow())
                    proc.Kill();
                proc.WaitForExit(5000);
            }
            catch { }
        }
        Thread.Sleep(500);

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit");
            if (key != null)
            {
                key.SetValue("LastKey", fullPath, RegistryValueKind.String);
                key.Flush();
            }
        }
        catch { }

        Thread.Sleep(200);

        try
        {
            Process.Start(new ProcessStartInfo { FileName = "regedit.exe", UseShellExecute = true });
        }
        catch { }
    }

    private static string DetectPrefix()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit");
            string? lastKey = key?.GetValue("LastKey") as string;
            if (!string.IsNullOrEmpty(lastKey))
            {
                int hkeyIdx = lastKey.IndexOf("HKEY_", StringComparison.Ordinal);
                if (hkeyIdx > 1)
                    return lastKey[..(hkeyIdx - 1)]; // e.g. "计算机" or "Computer"
                if (hkeyIdx == 0)
                    return ""; // no prefix
            }
        }
        catch { }

        // Fallback: guess from OS locale
        var culture = System.Globalization.CultureInfo.InstalledUICulture;
        if (culture.TwoLetterISOLanguageName == "zh") return "计算机";
        if (culture.TwoLetterISOLanguageName == "ja") return "コンピューター";
        if (culture.TwoLetterISOLanguageName == "ko") return "컴퓨터";
        return "Computer";
    }
}
