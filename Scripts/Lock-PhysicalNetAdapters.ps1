#Requires -RunAsAdministrator
<#
.SYNOPSIS
  锁定本机所有物理网卡：拦截「禁用」以及 IP/网关等配置写入。

.DESCRIPTION
  - 仅处理非 Virtual 网卡（虚拟机里的 Hyper-V/VirtIO/vmxnet 合成网卡算物理网卡）
  - 自动跳过 TUN/VPN/TAP/Wintun/Loopback/WAN Miniport 等
  - 做法：
      1) 收紧 PnP 设备 ACL → ncpa.cpl / Disable-NetAdapter 禁用会失败
      2) 在 Tcpip/Tcpip6/NetBT/连接/驱动类键上加 Administrators Deny 写 → 改 IP 会失败
  - 备份：%ProgramData%\WinNetManager\NetAdapterLock\backup.json
  - 还原请运行同目录 Unlock-PhysicalNetAdapters.ps1

.NOTES
  管理员仍保留 WRITE_DAC，因此可用解锁脚本还原；无需维护模式。
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$BackupDir  = Join-Path $env:ProgramData 'WinNetManager\NetAdapterLock'
$BackupFile = Join-Path $BackupDir 'backup.json'

$ExcludePattern = 'Wintun|WireGuard|Tailscale|TAP-Windows|\bTAP\b|\bTUN\b|VPN|Npcap|ZeroTier|OpenVPN|SoftEther|Cloudflare|WARP|Nebula|Hamachi|Loopback|WAN Miniport|Bluetooth|VirtualBox Host|Hyper-V Virtual Ethernet|Docker|vEthernet'

# SYSTEM 全控；Administrators 可读 + 改 ACL（便于解锁），但不能写设备状态（禁用不了）
$LockedDeviceSddl = 'O:SYG:SYD:P(A;;GA;;;SY)(A;;GRRCWD;;;BA)'

if (-not ('DevNodeSec' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DevNodeSec {
    public const uint CM_DRP_SECURITY = 0x00000017;
    public const int CR_SUCCESS = 0;
    public const int CR_BUFFER_SMALL = 0x1A;
    public const int OWNER_SECURITY_INFORMATION = 0x00000001;
    public const int GROUP_SECURITY_INFORMATION = 0x00000002;
    public const int DACL_SECURITY_INFORMATION = 0x00000004;
    public const int SDDL_REVISION_1 = 1;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNode(out uint devInst, string deviceID, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_DevNode_Registry_Property(
        uint devInst, uint property, IntPtr dataType, byte[] buffer, ref uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Set_DevNode_Registry_Property(
        uint devInst, uint property, byte[] buffer, uint length, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(
        byte[] sd, uint requestor, int securityInfo, out IntPtr str, out uint strLen);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string sddl, uint revision, out IntPtr sd, out uint sdLen);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr h);

    public static string GetSddl(string instanceId) {
        uint devInst;
        int r = CM_Locate_DevNode(out devInst, instanceId, 0);
        if (r != CR_SUCCESS) throw new Exception("CM_Locate_DevNode failed: 0x" + r.ToString("X") + " id=" + instanceId);

        uint len = 0;
        r = CM_Get_DevNode_Registry_Property(devInst, CM_DRP_SECURITY, IntPtr.Zero, null, ref len, 0);
        if (r != CR_BUFFER_SMALL && r != CR_SUCCESS)
            throw new Exception("CM_Get_DevNode_Registry_Property(size) failed: 0x" + r.ToString("X"));

        byte[] buf = new byte[len];
        r = CM_Get_DevNode_Registry_Property(devInst, CM_DRP_SECURITY, IntPtr.Zero, buf, ref len, 0);
        if (r != CR_SUCCESS) throw new Exception("CM_Get_DevNode_Registry_Property failed: 0x" + r.ToString("X"));

        IntPtr pStr;
        uint strLen;
        int info = OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION;
        if (!ConvertSecurityDescriptorToStringSecurityDescriptor(buf, SDDL_REVISION_1, info, out pStr, out strLen))
            throw new Exception("Convert SD->SDDL failed: " + Marshal.GetLastWin32Error());

        try { return Marshal.PtrToStringUni(pStr); }
        finally { LocalFree(pStr); }
    }

    public static void SetSddl(string instanceId, string sddl) {
        uint devInst;
        int r = CM_Locate_DevNode(out devInst, instanceId, 0);
        if (r != CR_SUCCESS) throw new Exception("CM_Locate_DevNode failed: 0x" + r.ToString("X"));

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

function Get-TargetAdapters {
    Get-NetAdapter -ErrorAction Stop | Where-Object {
        -not $_.Virtual -and
        $_.MacAddress -and
        $_.PnPDeviceID -and
        $_.InterfaceDescription -notmatch $ExcludePattern -and
        $_.Name -notmatch $ExcludePattern
    }
}

function Get-AdapterRegistryPaths {
    param($Adapter)

    $guidB = $Adapter.InterfaceGuid.ToString('B') # {xxxxxxxx-...}
    $paths = @(
        "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$guidB"
        "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\$guidB"
        "HKLM:\SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces\Tcpip_$guidB"
        "HKLM:\SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\$guidB\Connection"
    )

    $classRoot = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}'
    if (Test-Path $classRoot) {
        Get-ChildItem $classRoot -ErrorAction SilentlyContinue | ForEach-Object {
            $p = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
            if ($null -ne $p -and $p.NetCfgInstanceId -eq $Adapter.InterfaceGuid.ToString('B')) {
                $paths += $_.PSPath
            }
            elseif ($null -ne $p -and $p.NetCfgInstanceId -eq $Adapter.InterfaceGuid.ToString('D')) {
                $paths += $_.PSPath
            }
        }
    }

    $paths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -Unique
}

function Get-RegistrySddl {
    param([string]$Path)
    (Get-Acl -LiteralPath $Path).Sddl
}

function Set-RegistrySddl {
    param([string]$Path, [string]$Sddl)
    $acl = Get-Acl -LiteralPath $Path
    $acl.SetSecurityDescriptorSddlForm($Sddl)
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Lock-RegistryKey {
    param([string]$Path)

    $original = Get-RegistrySddl -Path $Path
    $acl = Get-Acl -LiteralPath $Path

    $denyRights =
        [System.Security.AccessControl.RegistryRights]::SetValue -bor
        [System.Security.AccessControl.RegistryRights]::CreateSubKey -bor
        [System.Security.AccessControl.RegistryRights]::Delete -bor
        [System.Security.AccessControl.RegistryRights]::WriteKey

    $admins = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $rule = [System.Security.AccessControl.RegistryAccessRule]::new(
        $admins,
        $denyRights,
        [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [System.Security.AccessControl.InheritanceFlags]::ObjectInherit,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Deny
    )

    # 避免重复叠加 Deny
    foreach ($existing in @($acl.GetAccessRules($true, $false, [System.Security.Principal.SecurityIdentifier]))) {
        if ($existing.AccessControlType -eq 'Deny' -and $existing.IdentityReference -eq $admins) {
            [void]$acl.RemoveAccessRuleSpecific($existing)
        }
    }

    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl
    return $original
}

# --- main ---
New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null

if (Test-Path -LiteralPath $BackupFile) {
    Write-Warning "已存在备份: $BackupFile"
    Write-Warning "若再次锁定，将覆盖备份。若当前已是锁定状态，请先运行 Unlock 脚本。"
    $ans = Read-Host "继续并覆盖备份? [y/N]"
    if ($ans -notmatch '^[Yy]') { exit 1 }
}

$adapters = @(Get-TargetAdapters)
if ($adapters.Count -eq 0) {
    Write-Error "未找到可锁定的物理网卡（已排除 Virtual / TUN / VPN 等）。"
}

$backup = [ordered]@{
    LockedAt = (Get-Date).ToString('o')
    Computer = $env:COMPUTERNAME
    Adapters = @()
}

Write-Host "将锁定 $($adapters.Count) 块物理网卡：" -ForegroundColor Cyan
$adapters | ForEach-Object { Write-Host ("  - {0}  ({1})" -f $_.Name, $_.InterfaceDescription) }

foreach ($a in $adapters) {
    $entry = [ordered]@{
        Name           = $a.Name
        InterfaceGuid  = $a.InterfaceGuid.ToString('B')
        PnPDeviceID    = $a.PnPDeviceID
        Description    = $a.InterfaceDescription
        DeviceSddl     = $null
        RegistryKeys   = @()
    }

    Write-Host "`n[$($a.Name)] 备份并锁定 PnP 设备 ACL..." -ForegroundColor Yellow
    $entry.DeviceSddl = [DevNodeSec]::GetSddl($a.PnPDeviceID)
    [DevNodeSec]::SetSddl($a.PnPDeviceID, $LockedDeviceSddl)
    Write-Host "  设备 ACL 已锁定"

    foreach ($regPath in (Get-AdapterRegistryPaths -Adapter $a)) {
        Write-Host "  锁定注册表: $regPath"
        $orig = Lock-RegistryKey -Path $regPath
        $entry.RegistryKeys += [ordered]@{ Path = $regPath; Sddl = $orig }
    }

    $backup.Adapters += $entry
}

$backup | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $BackupFile -Encoding UTF8
Write-Host "`n锁定完成。备份已写入:" -ForegroundColor Green
Write-Host "  $BackupFile"
Write-Host "`n验证建议（远程请谨慎）:"
Write-Host "  在「网络连接」里对上述网卡点「禁用」应失败；改 IP 也应失败。"
Write-Host "还原:"
Write-Host "  .\Unlock-PhysicalNetAdapters.ps1"
