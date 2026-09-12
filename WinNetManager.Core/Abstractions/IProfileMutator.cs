
namespace WinNetManager.Core.Abstractions;

/// <summary>
/// 网络配置文件变更（删除/重命名/备份）。生产实现包装 NetworkProfileService + RegistryBackupService。
/// 删除/重命名均按名称唯一命中；当前已连接的配置文件禁止删除。
/// </summary>
public interface IProfileMutator
{
    bool BackupProfiles(out string backupPath);
    bool DeleteProfileByName(string name, out string error);
    bool RenameProfileByName(string fromName, string toName, out string error);
}
