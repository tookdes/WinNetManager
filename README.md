# WinNetManager - Windows 网络管理工具

C# .NET 8 + WPF 实现的 Windows 网络管理 GUI，一个面向 Windows 网络管理的「瑞士军刀」式工具。用于解决有线/WiFi 网卡反复新建导致的"以太网 2"、"WLAN 3"、"网络 2"等命名混乱问题，以及管理 Windows 持久路由、清理幽灵设备等一站式网络运维需求。

## 设计哲学

Windows 上分散、难记、危险的网络命令，被包装成**可解释、可回滚、可测试**的规则系统：

- **可解释** — 每个操作都附带命令预览面板，精确展示底层 PowerShell/netsh/cmd 命令
- **可回滚** — 持久路由采用延迟应用（先标记 Added/Modified/Deleted，确认后再批量写入）
- **可测试** — 路由测试、端口测试、域名解析等诊断功能内置，修改前即可验证网络走向
- **可迁移** — 持久路由和端口转发支持 JSON 配置文件导入导出，跨系统迁移无忧

## 功能概览

| 标签页 | 功能 | 数据来源 |
|--------|------|----------|
| **网络配置文件** | 管理"网络"/"网络 2"等位置名称，切换公用/专用，批量删除历史网络 | 注册表 + COM INetworkListManager |
| **连接名称** | 管理"以太网"/"WLAN"/"本地连接 *2"等连接名称，重命名/删除 | 注册表 |
| **设备描述** | 重置设备描述实例编号（消除 #2 #3 后缀） | 注册表 (REG_MULTI_SZ) |
| **幽灵网卡** | 枚举所有网卡（含隐藏设备），批量卸载幽灵设备 | SetupDi* P/Invoke |
| **持久路由** | 管理 Windows 持久路由（IPv4/IPv6），新建/修改/删除，批量应用更改，路由测试 | PowerShell NetTCPIP |
| **端口转发** | 管理 `netsh interface portproxy` 端口转发规则，支持 IPv4/IPv6 四种方向，自动联动防火墙 | netsh |
| **IP 配置** | 网卡 DHCP 释放+续租（IPv4/IPv6），支持远程机器安全执行 | ipconfig |
| **DNS** | NRPT（名称解析策略表）管理，域名解析测试，DNS 缓存刷新 | PowerShell DnsClient |
| **网络诊断** | 端口连通性测试，Tracert/Pathping，当前 TCP 连接查看 | PowerShell NetTCPIP |

## 交互亮点

- **命令预览面板** — 底部可折叠面板，执行关键操作后自动展开，显示精确命令
- **空状态引导** — 数据为空时显示友好提示和操作快捷入口，而非空白表格
- **术语悬浮解释** — 关键列头（如 NRPT、PersistentStore、Metric）带 Tooltip 释义
- **干运行导入预览** — 导入 JSON 配置前，先展示「新增/修改/跳过」清单，确认后再执行
- **路由测试** — 输入目标 IP/域名，自动解析并显示匹配路由 + Ping 延迟统计
- **智能 CIDR 编辑器** — 新建/编辑路由时，前缀长度下拉 + 实时网段范围计算 + IP 范围计算器
- **防火墙联动** — 端口转发增删改时自动同步 Windows Defender 防火墙入站规则
- **右键菜单** — 包含所有操作项 + 复制选中值 + 在 RegEdit 中打开
- **自然排序** — 点击列头排序使用 `StrCmpLogicalW`（"连接 2" < "连接 10"）
- **行选择** — 点击选中，Ctrl+点击多选，Shift+点击范围选

## 三种"名字"的区别

```
+--------------------------------------------------------------------+
| Device Description（设备描述）                                      |
| 例: "Realtek PCIe GbE Family Controller #2"                       |
| -> Descriptions 注册表 REG_MULTI_SZ (实例编号列表)                   |
| -> #2 后缀由实例编号产生，清理幽灵设备 + 重置编号可消除              |
+--------------------------------------------------------------------+
| Connection Name（连接名称）                                        |
| 例: "以太网"、"WLAN 2"、"本地连接 *3"                              |
| -> Control\Network\{...}\{GUID}\Connection\Name                   |
| -> 直接改注册表即可重命名                                           |
+--------------------------------------------------------------------+
| Network Profile（网络位置/配置文件）                                |
| 例: "网络"、"网络 2"、"网络 3"                                     |
| -> NetworkList\Profiles\{GUID}\ProfileName                        |
| -> 改注册表或通过 INetworkListManager COM 接口操作                  |
| -> 同时控制公用/专用网络类型                                        |
+--------------------------------------------------------------------+
```

## 推荐操作流程

1. **备份** — 状态栏右下角"备份注册表"按钮，一键导出网络相关注册表
2. **幽灵网卡** — 先清理设备管理器中隐藏的旧设备
3. **设备描述** — 重置实例编号为单实例
4. **连接名称** — 删除无效连接条目，重命名剩余连接
5. **网络配置文件** — 删除历史网络，重命名当前网络
6. **持久路由** — 检查并整理系统持久路由表（IPv4/IPv6），使用路由测试验证走向
7. **端口转发** — 检查并整理 `netsh interface portproxy` 端口转发规则
8. **IP 配置** — 必要时对指定网卡执行 DHCP Release+Renew
9. **DNS** — 配置 NRPT 规则解决双网卡 DNS 污染，刷新 DNS 缓存
10. **重启** — 部分更改需要重启或重新插拔网卡生效

## 编译与运行

```powershell
cd WinNetManager
dotnet build              # Debug 编译
dotnet publish -c Release # Release 发布（依赖框架）

# 自包含单文件发布（无需目标机器安装 .NET）
dotnet publish -c Release --self-contained -r win-x64 -p:PublishSingleFile=true
```

**必须以管理员身份运行**（已在 app.manifest 中声明 `requireAdministrator`）。

## 注册表路径对照表

| 概念 | 注册表路径 | 关键值 |
|------|-----------|--------|
| 网络配置文件 (Profile) | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles\{GUID}` | `ProfileName` (REG_SZ), `Category` (DWORD: 0=公用, 1=专用, 2=域) |
| 网络签名 (Signature) | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Signatures\Unmanaged\{GUID}` | `ProfileGuid`, `FirstNetwork`, `DefaultGatewayMac` |
| 连接名称 (Connection) | `HKLM\SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\{GUID}\Connection` | `Name` (REG_SZ), `PnpInstanceID` |
| 设备描述实例编号 | `HKLM\SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\Descriptions` | 属性名=设备描述, 值=REG_MULTI_SZ (实例编号列表，如 ["1","2"]) |
| TCP/IP 接口参数 | `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{GUID}` | IP 配置 |

## 技术细节

- **COM**: 使用 `INetworkListManager` (CLSID `{DCB00C01-570F-4A9B-8D69-199FDBA5723B}`) 通过 dynamic 后期绑定调用
- **P/Invoke**: 使用 `SetupDi*` API 族枚举设备树，`CM_Get_DevNode_Status` 判断设备是否存在
- **PowerShell**: 通过 `Get-NetRoute` / `New-NetRoute` / `Remove-NetRoute` (NetTCPIP 模块) 管理持久路由，`Find-NetRoute` 做路由测试；`-EncodedCommand` + Base64 避免命令注入
- **DNS NRPT**: 通过 `Add-DnsClientNrptRule` / `Get-DnsClientNrptRule` / `Remove-DnsClientNrptRule` 管理条件解析
- **注册表**: 使用 `Microsoft.Win32.Registry` 读写，Descriptions 键为 REG_MULTI_SZ 格式
- **导入导出**: JSON 配置文件格式，支持持久路由和端口转发的跨系统迁移，带版本校验
- **RegEdit 导航**: 自动检测本地化前缀（中文"计算机"/英文"Computer"），通过 LastKey 定位
- **自然排序**: 通过 P/Invoke `shlwapi.dll!StrCmpLogicalW` 实现，列头排序也使用 `ListCollectionView.CustomSort`
- **进程执行**: 统一使用 `ProcessRunner`，异步读取 stdout/stderr 避免死锁，超时自动 Kill
