# TrafficCollector
🤖 AI 生成声明
本项目代码的 90% 以上由 DeepSeek 生成，少量错误逻辑和边界问题由作者发现并反馈修正。

作者角色：

🏗️ 项目框架设计
⚙️ 编译参数与部署配置设计
🧪 运行测试与 Bug 反馈
🔄 人机协作流程协调
这是一个典型的 AI 辅助编程实践项目。展示了当前大语言模型在渐进式重构和生产化改造中的能力与局限性。通过人机协作，大幅提升了开发效率，同时保证了代码质量。

一个基于 Windows ETW 的轻量级流量采集库，面向 .NET Framework 4.7.2。只提供采集能力，不提供 UI、控制台或服务宿主，界面与生命周期完全交给调用方。

## 是什么

- 基于 `KernelTraceEventParser` 的 `NetworkTCPIP` 事件，按 PID 统计本机进程的 TCP / UDP 收发字节数
- 每 3 秒生成一次增量快照，通过 `OnSnapshotCaptured` 事件交给调用方，同时批量写入本地 SQLite
- 只累加数据包大小，**不解析 payload，不向任何外部地址发送数据**
- 已处理 PID 复用、ETW 幽灵会话、进程退出后的状态清理等常见问题

## 不是什么

- 不是抓包工具，不解析内容
- 不是生产级计费 / 告警 / 长期监控系统
- 不提供 UI、托盘、开机自启、数据上报
- 不保证 `Stop` 时所有未落库数据都写入
- 不是 .NET Core / .NET 5+ 库，使用方需自行评估兼容性

适合本机流量观测、网络程序调试、代理链路验证等测试场景，不适合长期常驻监控。

## 环境要求

- Windows，**需要管理员权限**（ETW 采集要求）
- .NET Framework 4.7.2+
- 使用前需自行安装以下 NuGet 包（库不内置、不提供预编译包）：
  - `Microsoft.Data.Sqlite` —— 本地数据库写入
  - `Microsoft.Diagnostics.Tracing.TraceEvent` —— ETW 事件解析

> 库不内置依赖，也不提供预编译包。请通过 NuGet 还原后自行编译。

## 快速开始

```csharp
using TrafficCollector;

var collector = new TrafficMonitorCore();

collector.OnLogMessage += msg => Console.WriteLine(msg);
collector.OnSnapshotCaptured += rows =>
{
    foreach (var r in rows)
        Console.WriteLine($"{r.ProcessName}({r.Pid}) 发 {r.SentBytes} / 收 {r.RecvBytes}");
};

collector.Start("TrafficStats.db");

Console.CancelKeyPress += (s, e) => { e.Cancel = true; collector.Stop(); };
Thread.Sleep(Timeout.Infinite);
```

## 公开 API

| 成员 | 说明 |
|---|---|
| `Start(string dbPath)` | 初始化数据库并启动采集；需要管理员权限。失败时通过 `OnLogMessage` 报告 |
| `Stop()` | 停止采集并尽力刷盘；可重复调用 |
| `IsRunning` | 当前是否正在采集 |
| `event OnLogMessage` | 运行日志，调用方自行决定写文件 / 打印 |
| `event OnSnapshotCaptured` | 每 3 秒触发的增量快照（`List<TrafficRecord>`） |
| `TrafficRecord` | `Timestamp` / `Pid` / `ProcessName` / `SentBytes` / `RecvBytes` |
| `TrafficMonitorCore` | 实现 `IDisposable`，`Dispose()` 等价于 `Stop()` |

## 数据库 Schema

`Start(dbPath)` 会在指定路径创建（或复用）SQLite 数据库：

```sql
CREATE TABLE TrafficStats (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp   TEXT    NOT NULL,   -- yyyy-MM-dd HH:mm:ss（本地时间）
    Pid         INTEGER NOT NULL,
    ProcessName TEXT,
    SentBytes   INTEGER NOT NULL,   -- 字节
    RecvBytes   INTEGER NOT NULL    -- 字节
);
```

- 时间格式：`yyyy-MM-dd HH:mm:ss`，本地时间，无时区信息
- 单位：字节
- 库只负责写入，不负责清理 / 轮转，数据保留策略由调用方决定

## 宿主的 app.config 需要合并的 binding redirect

库自带的 `app.config` **不会**在别人引用时自动生效。请将以下内容合并到**宿主进程**的 `app.config`，否则可能运行时出现 `Could not load file or assembly 'System.Memory, ...'` 等错误：

```xml
<runtime>
  <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
    <dependentAssembly>
      <assemblyIdentity name="System.Memory" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
      <bindingRedirect oldVersion="0.0.0.0-4.0.5.0" newVersion="4.0.5.0" />
    </dependentAssembly>
    <!-- System.Buffers / System.Collections.Immutable / System.Runtime.CompilerServices.Unsafe /
         System.Threading.Tasks.Extensions / System.Text.Json 等同理，见仓库 app.config -->
  </assemblyBinding>
</runtime>
```

完整列表见仓库根目录的 `app.config`。

## 许可

MIT。

仅用于合法合规的本机流量观测、网络调试与学习研究。使用者需遵守所在地区法律法规。
