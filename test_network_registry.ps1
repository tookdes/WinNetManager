# WinNetManager 测试脚本 - 以管理员身份运行
# chcp 65001

Write-Host "=== WinNetManager 测试脚本 ===" -ForegroundColor Cyan
Write-Host ""

# 1. Network Profiles
Write-Host "[1] 网络配置文件 (Profiles)" -ForegroundColor Yellow
$profilesPath = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles"
if (Test-Path $profilesPath) {
    $profiles = Get-ChildItem -Path $profilesPath
    Write-Host "  找到 $($profiles.Count) 个配置文件:"
    foreach ($p in $profiles) {
        $props = Get-ItemProperty -Path $p.PSPath
        $category = switch ($props.Category) { 0 {"公用"} 1 {"专用"} 2 {"域"} default {"未知"} }
        Write-Host "  - $($props.ProfileName) [$category] (GUID: $($p.PSChildName))"
    }
} else { Write-Host "  路径不存在" -ForegroundColor Red }
Write-Host ""

# 2. Connection Names
Write-Host "[2] 连接名称 (Connections)" -ForegroundColor Yellow
$netClassPath = "Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}"
if (Test-Path $netClassPath) {
    $connCount = 0
    foreach ($sk in (Get-ChildItem -Path $netClassPath)) {
        $connPath = "$($sk.PSPath)\Connection"
        if (Test-Path $connPath) {
            $props = Get-ItemProperty -Path $connPath
            if ($props.Name) {
                $connCount++
                $pnp = if ($props.PnpInstanceID) { $props.PnpInstanceID } else { "(无)" }
                Write-Host "  - $($props.Name) | PnpID: $pnp | GUID: $($sk.PSChildName)"
            }
        }
    }
    Write-Host "  共 $connCount 个连接"
} else { Write-Host "  路径不存在" -ForegroundColor Red }
Write-Host ""

# 3. Descriptions (REG_MULTI_SZ)
Write-Host "[3] 设备描述实例编号 (Descriptions)" -ForegroundColor Yellow
$descPath = "$netClassPath\Descriptions"
if (Test-Path $descPath) {
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\Descriptions")
    foreach ($name in $key.GetValueNames()) {
        if ($name) {
            $val = $key.GetValue($name)
            $kind = $key.GetValueKind($name)
            if ($val -is [string[]]) {
                Write-Host "  - $name = [$($val -join ', ')] ($kind)"
            } else {
                Write-Host "  - $name = $val ($kind)"
            }
        }
    }
    $key.Close()
} else { Write-Host "  路径不存在" -ForegroundColor Red }
Write-Host ""

# 4. COM INetworkListManager
Write-Host "[4] COM INetworkListManager" -ForegroundColor Yellow
try {
    $nlm = [Activator]::CreateInstance([Type]::GetTypeFromCLSID([Guid]"DCB00C01-570F-4A9B-8D69-199FDBA5723B"))
    Write-Host "  已连接: $($nlm.IsConnected) | Internet: $($nlm.IsConnectedToInternet)"
    foreach ($net in $nlm.GetNetworks(1)) {
        $cat = switch ($net.GetCategory()) { 0 {"公用"} 1 {"专用"} 2 {"域"} default {"未知"} }
        Write-Host "  - $($net.GetName()) [$cat]"
    }
} catch { Write-Host "  COM 调用失败: $($_.Exception.Message)" -ForegroundColor Red }
Write-Host ""

# 5. 网络适配器设备 (PnP)
Write-Host "[5] 网络适配器设备" -ForegroundColor Yellow
try {
    $devices = Get-PnpDevice -Class Net -ErrorAction SilentlyContinue
    if ($devices) {
        $present = ($devices | Where-Object { $_.Status -eq 'OK' }).Count
        $ghost = ($devices | Where-Object { $_.Status -ne 'OK' }).Count
        Write-Host "  活跃: $present | 幽灵: $ghost"
        foreach ($d in $devices) {
            $s = if ($d.Status -eq 'OK') { '活跃' } else { '幽灵' }
            Write-Host "  - [$s] $($d.FriendlyName)"
        }
    }
} catch { Write-Host "  PnP 枚举失败: $($_.Exception.Message)" -ForegroundColor Red }
Write-Host ""

# 6. 活跃适配器 GUID 匹配测试
Write-Host "[6] NetworkInterface GUID 匹配" -ForegroundColor Yellow
try {
    [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | ForEach-Object {
        Write-Host "  - $($_.Name) | ID: $($_.Id) | Status: $($_.OperationalStatus)"
    }
} catch { Write-Host "  枚举失败: $($_.Exception.Message)" -ForegroundColor Red }
Write-Host ""

Write-Host "=== 测试完成 ===" -ForegroundColor Cyan
