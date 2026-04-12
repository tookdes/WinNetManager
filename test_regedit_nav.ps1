# 测试 RegEdit 导航 - 以管理员身份运行

$targetPath = "Computer\HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\Descriptions"
$regPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"

Write-Host "=== RegEdit 导航测试 ===" -ForegroundColor Cyan
Write-Host ""

# Step 1: 读取当前 LastKey
$current = (Get-ItemProperty -Path $regPath -Name "LastKey" -ErrorAction SilentlyContinue).LastKey
Write-Host "[1] 当前 LastKey:" -ForegroundColor Yellow
Write-Host "    $current"
Write-Host ""

# Step 2: 关闭所有 regedit
Write-Host "[2] 关闭所有 regedit 实例..." -ForegroundColor Yellow
Get-Process regedit -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# Step 3: 重新读取 LastKey
$afterClose = (Get-ItemProperty -Path $regPath -Name "LastKey" -ErrorAction SilentlyContinue).LastKey
Write-Host "[3] regedit 关闭后 LastKey:" -ForegroundColor Yellow
Write-Host "    $afterClose"
Write-Host ""

# Step 4: 写入目标路径
Write-Host "[4] 写入目标路径:" -ForegroundColor Yellow
Write-Host "    $targetPath"
Set-ItemProperty -Path $regPath -Name "LastKey" -Value $targetPath -Type String

# Step 5: 读回验证
Start-Sleep -Milliseconds 200
$written = (Get-ItemProperty -Path $regPath -Name "LastKey").LastKey
Write-Host "[5] 写入后读回:" -ForegroundColor Yellow
Write-Host "    $written"
Write-Host ""

# Step 6: 启动 regedit
Start-Sleep -Milliseconds 500
Write-Host "[6] 启动 regedit..." -ForegroundColor Yellow
Start-Process regedit.exe
Write-Host ""
Write-Host "=== 请检查 regedit 是否导航到了正确位置 ===" -ForegroundColor Cyan
