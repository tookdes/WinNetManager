#Requires -RunAsAdministrator
<#
.SYNOPSIS
  还原 Lock-PhysicalNetAdapters.ps1 对物理网卡施加的 ACL 锁定。

.DESCRIPTION
  读取 %ProgramData%\WinNetManager\NetAdapterLock\backup.json，
  按备份恢复 PnP 设备 ACL 与相关注册表 ACL。
#>

[CmdletBinding()]
param(
    [string]$BackupFile = $(Join-Path $env:ProgramData 'WinNetManager\NetAdapterLock\backup.json')
)

$ErrorActionPreference = 'Stop'

if (-not ('DevNodeSecUnlock' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class DevNodeSecUnlock {
    public const uint CM_DRP_SECURITY = 0x00000017;
    public const int CR_SUCCESS = 0;
    public const int SDDL_REVISION_1 = 1;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNode(out uint devInst, string deviceID, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Set_DevNode_Registry_Property(
        uint devInst, uint property, byte[] buffer, uint length, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string sddl, uint revision, out IntPtr sd, out uint sdLen);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr h);

    public static void SetSddl(string instanceId, string sddl) {
        uint devInst;
        int r = CM_Locate_DevNode(out devInst, instanceId, 0);
        if (r != CR_SUCCESS) throw new Exception("CM_Locate_DevNode failed: 0x" + r.ToString("X") + " id=" + instanceId);

        IntPtr pSd;
        uint sdLen;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SDDL_REVISION_1, out pSd, out sdLen))
            throw new Exception("Convert SDDL->SD failed: " + Marshal.GetLastWin32Error());

        try {
            byte[] buf = new byte[sdLen];
            Marshal.Copy(pSd, buf, 0, (int)sdLen);
            r = CM_Set_DevNode_Registry_Property(devInst, CM_DRP_SECURITY, buf, sdLen, 0);
            if (r != CR_SUCCESS) throw new Exception("CM_Set_DevNode_Registry_Property failed: 0x" + r.ToString("X"));
        }
        finally { LocalFree(pSd); }
    }
}
'@
}

function Set-RegistrySddl {
    param([string]$Path, [string]$Sddl)
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Warning "注册表路径不存在，跳过: $Path"
        return
    }
    $acl = Get-Acl -LiteralPath $Path
    $acl.SetSecurityDescriptorSddlForm($Sddl)
    Set-Acl -LiteralPath $Path -AclObject $acl
}

if (-not (Test-Path -LiteralPath $BackupFile)) {
    Write-Error "找不到备份文件: $BackupFile`n请确认曾成功运行过 Lock-PhysicalNetAdapters.ps1"
}

$backup = Get-Content -LiteralPath $BackupFile -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $backup.Adapters -or $backup.Adapters.Count -eq 0) {
    Write-Error "备份文件无适配器记录: $BackupFile"
}

Write-Host "将从备份还原 $($backup.Adapters.Count) 块网卡（锁定于 $($backup.LockedAt)）" -ForegroundColor Cyan

foreach ($entry in $backup.Adapters) {
    Write-Host "`n[$($entry.Name)] 还原 PnP 设备 ACL..." -ForegroundColor Yellow
    try {
        [DevNodeSecUnlock]::SetSddl($entry.PnPDeviceID, $entry.DeviceSddl)
        Write-Host "  设备 ACL 已还原"
    }
    catch {
        Write-Warning "  设备 ACL 还原失败: $($_.Exception.Message)"
    }

    foreach ($reg in $entry.RegistryKeys) {
        Write-Host "  还原注册表: $($reg.Path)"
        try {
            Set-RegistrySddl -Path $reg.Path -Sddl $reg.Sddl
        }
        catch {
            Write-Warning "  注册表还原失败: $($_.Exception.Message)"
        }
    }
}

Write-Host "`n还原完成。" -ForegroundColor Green
Write-Host "如需重新锁定，再运行 Lock-PhysicalNetAdapters.ps1"
Write-Host "备份文件仍保留于: $BackupFile"
