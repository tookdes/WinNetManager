## v0.9

### 新增
- **多网卡回源与防火墙**：新增「回源默认路由」向导，只对指定网卡增加高跃点 IPv4/IPv6 默认路由，可切换强/弱主机模型；新增独立「防火墙」标签页，支持按网卡与端口范围创建入站允许规则
- **网卡协议开关**：新增 IPv6 隐私地址、IPv6 绑定与 NetBIOS 等常用网卡开关，集中管理容易分散在系统各处的网络参数
- **DNS 接管诊断**：DNS 解析失败后自动执行快速诊断，检测 Proxifier、Clash/Mihomo、sing-box、v2rayN、WinDivert/TUN 等可能影响解析与流量路径的组件，并在 DNS 页面直接给出提示
- **离线深度诊断包**：新增一键生成 ZIP 的全量网络现场采集，覆盖 IP/DHCP、DNS/NRPT/DoH、网关/路由/邻居、代理/VPN、WFP/防火墙、TCP/UDP、事件日志、接口错误与流量采样
- **故障目标专项探测**：生成诊断包时可复用「目标/端口」输入，对实际故障域名、IP 或 URL 追加 DNS、Ping、TCP、直连/系统代理 HTTP(S) 与 IPv4/IPv6 Traceroute；PktMon 抓包支持 10/30/60 秒

### 优化
- **诊断可信度**：采集步骤区分 OK / EMPTY / SKIP / WARN / FAIL，manifest 记录退出码、耗时、文件大小/行数与 PowerShell 错误计数，避免把“采集失败”误读成“配置不存在”
- **IPv4/IPv6 判读**：修正 IPv6-only、ULA、网关禁 Ping、公网 ICMP 被过滤、DNS 超时等场景的误报，并增加真正的 IPv6 Traceroute
- **诊断性能**：PowerShell 采集采用有限并发，Traceroute/WFP 等耗时步骤并行化；支持取消、180 秒总时间预算、原子 ZIP 输出与磁盘空间预检
- **兼容性与可读性**：原生命令使用真实 Windows OEM 代码页读取，修复中文系统输出乱码；补充 WLAN/802.1X/WWAN、NLA/WFP/Firewall/DHCPv6 等专用证据并对不适用项自动 SKIP

### 修复
- **WinHTTP 代理乱码**：改用原生 API 获取 WinHTTP 代理状态，避免本地化命令输出解析错误
- **回源路由边界**：修复默认路由前缀、重复下一跳、跃点更新与 PPPoE/on-link 等特殊拓扑下的判断问题，不再误改其他网卡默认路由
- **采集稳定性**：修复无效 `driverquery /SVC`、高连接数时逐条查进程导致的超时，以及可选事件通道/WWAN/802.1X 不存在时产生的噪声警告
- **自动判读误报**：DNS 未完成解析时不再显示虚假的“100% 丢包”，HTTP 直连/系统代理、NCSI、PktMon/WFP 部分失败均改为更准确的分层结果
