# TrafficCollector

一个 .NET Framework 4.7.2 的流量采集库，只提供采集能力，不提供 UI/控制台/服务宿主。

## 是什么
- 基于 Windows ETW（KernelTraceEventParser，NetworkTCPIP）按 PID 统计 TCP/UDP 收发字节数
- 每 3 秒快照一次增量，写入本地 SQLite（TrafficStats.db）
- 只统计字节数，不解析 payload，不外发任何数据

## 不是什么
- 不做 UI、托盘、开机自启、数据上报、长期监控
- 不是生产级计费/告警工具
- 不保证 Stop 时所有未落库数据都写入

## 环境要求
- Windows + 管理员权限（ETW 需要）
- .NET Framework 4.7.2+
- NuGet 包见 packages.config / PackageReference

## 快速开始
（20 行控制台示例，调 Start/Stop，订阅 OnSnapshotCaptured）

## 宿主 app.config 需要合并的 binding redirect
（贴 app.config 片段）

## 数据库 schema
表 TrafficStats(Id, Timestamp, Pid, ProcessName, SentBytes, RecvBytes)
时间格式：yyyy-MM-dd HH:mm:ss（本地时间）
单位：字节

## API
- Start(string dbPath) / Stop() / IsRunning
- event OnLogMessage / OnSnapshotCaptured
- TrafficRecord { Timestamp, Pid, ProcessName, SentBytes, RecvBytes }

## 许可与用途
MIT。仅用于本机流量观测、网络调试、学习研究，使用者需遵守所在地区法律法规。
