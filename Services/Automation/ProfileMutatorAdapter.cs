
using System.IO;
using WinNetManager.Core.Abstractions;

namespace WinNetManager.Services.Automation;

/// <summary>
/// IProfileMutator 生产实现：删除/重命名/备份网络配置文件。
/// 按名称唯一命中；当前已连接的配置文件禁止删除。
/// </summary>
public sealed class ProfileMutatorAdapter : IProfileMutator
{
    private const string ProfilesKeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList";

    public bool BackupProfiles(out string backupPath)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinNetManager", "backups");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"NetworkList_{DateTime.Now:yyyyMMdd_HHmmss}.reg");
            RegistryBackupService.BackupKeyToPath(ProfilesKeyPath, file);
            backupPath = file;
            return true;
        }
        catch (Exception)
        {
            backupPath = "";
            return false;
        }
    }

    public bool DeleteProfileByName(string name, out string error)
    {
        var profiles = NetworkProfileService.GetAllProfiles();
        var matches = profiles.Where(p => string.Equals(p.ProfileName, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
        {
            error = $"「{name}」匹配 {matches.Count} 个配置文件（需恰好 1 个），已拒绝删除。";
            return false;
        }
        var connected = NetworkListManagerService.GetConnectedNetworkIds();
        if (connected.Contains(matches[0].Guid))
        {
            error = $"「{name}」当前已连接，禁止删除。";
            return false;
        }
        try
        {
            NetworkProfileService.DeleteProfile(matches[0].Guid);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool RenameProfileByName(string fromName, string toName, out string error)
    {
        var profiles = NetworkProfileService.GetAllProfiles();
        var matches = profiles.Where(p => string.Equals(p.ProfileName, fromName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
        {
            error = $"「{fromName}」匹配 {matches.Count} 个配置文件（需恰好 1 个），已拒绝重命名。";
            return false;
        }
        if (profiles.Any(p => p.Guid != matches[0].Guid && string.Equals(p.ProfileName, toName, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"目标名称「{toName}」已存在其他配置文件，已拒绝重命名。";
            return false;
        }
        try
        {
            NetworkProfileService.RenameProfile(matches[0].Guid, toName);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
