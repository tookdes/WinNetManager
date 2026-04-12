# WinNetManager - Windows 网络名称管理工具

C# .NET 8 + WPF 实现的 Windows 网络名称管理 GUI，用于解决有线/WiFi 网卡反复新建导致的"以太网 2"、"WLAN 3"、"网络 2"等命名混乱问题。

## 功能概览

| 标签页 | 功能 | 数据来源 |
|--------|------|----------|
| 网络配置文件 | 管理"网络"/"网络 2"等位置名称，切换公用/专用，批量删除历史网络 | 注册表 + COM INetworkListManager |
| 连接名称 | 管理"以太网"/"WLAN"/"本地连接 *2"等连接名称，重命名/删除 | 注册表 |
| 设备描述 | 重置设备描述实例编号（消除 #2 #3 后缀） | 注册表 (REG_MULTI_SZ) |
| 幽灵网卡 | 枚举所有网卡（含隐藏设备），批量卸载幽灵设备 | SetupDi* P/Invoke |

## 注册表路径对照表

| 概念 | 注册表路径 | 关键值 |
|------|-----------|--------|
| 网络配置文件 (Profile) | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles\{GUID}` | `ProfileName` (REG_SZ), `Category` (DWORD: 0=公用, 1=专用, 2=域) |
| 网络签名 (Signature) | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Signatures\Unmanaged\{GUID}` | `ProfileGuid`, `FirstNetwork`, `DefaultGatewayMac` |
| 连接名称 (Connection) | `HKLM\SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\{GUID}\Connection` | `Name` (REG_SZ), `PnpInstanceID` |
| 设备描述实例编号 | `HKLM\SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\Descriptions` | 属性名=设备描述, 值=REG_MULTI_SZ (实例编号列表，如 ["1","2"]) |
| TCP/IP 接口参数 | `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{GUID}` | IP 配置 |

## 三种"名字"的区别

```
┌────────────────────────────────────────────────────────────────────┐
│ Device Description（设备描述）                                      │
│ 例: "Realtek PCIe GbE Family Controller #2"                       │
│ → Descriptions 注册表 REG_MULTI_SZ (实例编号列表)                   │
│ → #2 后缀由实例编号产生，清理幽灵设备 + 重置编号可消除              │
├────────────────────────────────────────────────────────────────────┤
│ Connection Name（连接名称）                                        │
│ 例: "以太网"、"WLAN 2"、"本地连接 *3"                              │
│ → Control\Network\{...}\{GUID}\Connection\Name                   │
│ → 直接改注册表即可重命名                                           │
├────────────────────────────────────────────────────────────────────┤
│ Network Profile（网络位置/配置文件）                                │
│ 例: "网络"、"网络 2"、"网络 3"                                     │
│ → NetworkList\Profiles\{GUID}\ProfileName                        │
│ → 改注册表或通过 INetworkListManager COM 接口操作                  │
│ → 同时控制公用/专用网络类型                                        │
└────────────────────────────────────────────────────────────────────┘
```

## 推荐操作流程

1. **备份** — 状态栏右下角"备份注册表"按钮
2. **幽灵网卡** — 先清理设备管理器中隐藏的旧设备
3. **设备描述** — 重置实例编号为单实例
4. **连接名称** — 删除无效连接条目，重命名剩余连接
5. **网络配置文件** — 删除历史网络，重命名当前网络
6. **重启** — 部分更改需要重启或重新插拔网卡生效

## 编译与运行

```powershell
cd WinNetManager
dotnet build              # Debug 编译
dotnet publish -c Release # Release 发布（依赖框架）

# 自包含单文件发布（无需目标机器安装 .NET）
dotnet publish -c Release --self-contained -r win-x64 -p:PublishSingleFile=true
```

**必须以管理员身份运行**（已在 app.manifest 中声明 `requireAdministrator`）。

## 快捷功能

- **状态栏按钮**: 网络适配器 (ncpa.cpl)、设备管理器 (devmgmt.msc)、备份注册表
- **右键菜单**: 复制选中值、在 RegEdit 中打开（自动检测中文/英文 Windows 前缀）
- **单击勾选**: CheckBox 单击即勾选，无需双击

## 技术细节

- **COM**: 使用 `INetworkListManager` (CLSID `{DCB00C01-570F-4A9B-8D69-199FDBA5723B}`) 通过 dynamic 后期绑定调用
- **P/Invoke**: 使用 `SetupDi*` API 族枚举设备树，`CM_Get_DevNode_Status` 判断设备是否存在
- **注册表**: 使用 `Microsoft.Win32.Registry` 读写，Descriptions 键为 REG_MULTI_SZ 格式
- **RegEdit 导航**: 自动检测本地化前缀（中文"计算机"/英文"Computer"），通过 LastKey 定位
