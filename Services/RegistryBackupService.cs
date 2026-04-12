using System.Diagnostics;
using System.IO;

namespace WinNetManager.Services;

public static class RegistryBackupService
{
    public static string BackupKeyToPath(string registryPath, string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var psi = new ProcessStartInfo
        {
            FileName = "reg.exe",
            Arguments = $"export \"{registryPath}\" \"{filePath}\" /y",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        proc?.WaitForExit(10000);

        return filePath;
    }
}
