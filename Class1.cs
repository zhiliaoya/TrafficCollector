using Microsoft.Data.Sqlite;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TrafficCollector
{
    public class TrafficRecord
    {
        public DateTime Timestamp { get; set; }
        public int Pid { get; set; }
        public string ProcessName { get; set; }
        public long SentBytes { get; set; }
        public long RecvBytes { get; set; }
    }

    public class TrafficMonitorCore : IDisposable
    {
        private readonly ConcurrentDictionary<int, long> _sentByPid = new ConcurrentDictionary<int, long>();
        private readonly ConcurrentDictionary<int, long> _recvByPid = new ConcurrentDictionary<int, long>();
        private readonly ConcurrentDictionary<int, long> _lastSent = new ConcurrentDictionary<int, long>();
        private readonly ConcurrentDictionary<int, long> _lastRecv = new ConcurrentDictionary<int, long>();
        private readonly ConcurrentDictionary<int, string> _nameCache = new ConcurrentDictionary<int, string>();

        private long _totalSent = 0;
        private long _totalReceived = 0;
        private readonly BlockingCollection<TrafficRecord> _writeQueue = new BlockingCollection<TrafficRecord>();

        private TraceEventSession _session;
        private Thread _writerThread;
        private CancellationTokenSource _cts;
        private string _dbPath;

        // 对外暴露事件，UI 或 Console 可以订阅用于打印日志
        public event Action<string> OnLogMessage;
        public event Action<List<TrafficRecord>> OnSnapshotCaptured;

        public bool IsRunning { get; private set; }

        static TrafficMonitorCore()
        {
            // 初始化 SQLite 原生库
            SQLitePCL.Batteries_V2.Init();
        }

        public void Start(string dbPath)
        {
            if (IsRunning) return;
            if (!(TraceEventSession.IsElevated() ?? false))
                throw new InvalidOperationException("采集需要管理员权限。");

            _dbPath = dbPath;
            InitDatabase();

            _cts = new CancellationTokenSource();

            // 启动写库线程
            _writerThread = new Thread(DbWriterLoop) { IsBackground = true };
            _writerThread.Start();

            // 使用固定的会话名称，避免幽灵会话无限累积
            string sessionName = "MyTrafficMonitorSession";

            // 尝试清理之前可能残留的同名会话
            try
            {
                using (var oldSession = new TraceEventSession(sessionName))
                {
                    oldSession.Stop(); // 如果存在，强制停止
                }
            }
            catch
            {
                // 如果不存在或发生异常，忽略即可
            }

            // 重新创建会话
            _session = new TraceEventSession(sessionName);

            try
            {
                _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            }
            catch (Exception ex)
            {
                // 如果系统内核 ETW 名额确实被其他程序占满，记录日志并退出
                OnLogMessage?.Invoke($"启动 ETW 失败，可能系统 ETW 会话已满: {ex.Message}");
                _session.Dispose();
                _cts.Cancel();
                _writeQueue.CompleteAdding();
                return;
            }

            // ---- 挂载事件 ----
            // TCP
            _session.Source.Kernel.TcpIpSend += data => Accumulate(data.ProcessID, data.size, true);
            _session.Source.Kernel.TcpIpRecv += data => Accumulate(data.ProcessID, data.size, false);

            // UDP IPv4
            _session.Source.Kernel.UdpIpSend += data => Accumulate(data.ProcessID, data.size, true);
            _session.Source.Kernel.UdpIpRecv += data => Accumulate(data.ProcessID, data.size, false);

            // UDP IPv6
            _session.Source.Kernel.UdpIpSendIPV6 += data => Accumulate(data.ProcessID, data.size, true);
            _session.Source.Kernel.UdpIpRecvIPV6 += data => Accumulate(data.ProcessID, data.size, false);

            // 进程名缓存
            _session.Source.Kernel.ProcessStart += data =>
            {
                _nameCache[data.ProcessID] = data.ImageFileName ?? "未知";
            };

            // 进程退出：清理累计值，防止 PID 复用
            _session.Source.Kernel.ProcessStop += data =>
            {
                int pid = data.ProcessID;
                _sentByPid.TryRemove(pid, out _);
                _recvByPid.TryRemove(pid, out _);
                _lastSent.TryRemove(pid, out _);
                _lastRecv.TryRemove(pid, out _);
                _nameCache.TryRemove(pid, out _);
            };

            // 定时处理线程（每 3 秒抓取一次增量并入队）
            Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(3000, _cts.Token);
                        ProcessSnapshot();
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        OnLogMessage?.Invoke($"[定时处理异常] {ex.Message}");
                    }
                }
            });

            // 启动 ETW 实时处理引擎（阻塞直到 Stop 被调用）
            Task.Run(() => _session.Source.Process());

            IsRunning = true;
            OnLogMessage?.Invoke("采集端已启动，正在监听网络...");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            _session?.Stop();
            _session?.Dispose();
            _writeQueue.CompleteAdding();
            _writerThread?.Join(2000);
            IsRunning = false;
            OnLogMessage?.Invoke("采集端已停止。");
        }

        private void Accumulate(int pid, long size, bool isSend)
        {
            if (isSend)
            {
                Interlocked.Add(ref _totalSent, size);
                _sentByPid.AddOrUpdate(pid, size, (_, old) => old + size);
            }
            else
            {
                Interlocked.Add(ref _totalReceived, size);
                _recvByPid.AddOrUpdate(pid, size, (_, old) => old + size);
            }
        }

        private void ProcessSnapshot()
        {
            var ts = DateTime.Now;
            var allPids = _sentByPid.Keys.Union(_recvByPid.Keys).ToList();

            var rows = allPids.Select(pid =>
            {
                _sentByPid.TryGetValue(pid, out var sent);
                _recvByPid.TryGetValue(pid, out var recv);
                _lastSent.TryGetValue(pid, out var prevSent);
                _lastRecv.TryGetValue(pid, out var prevRecv);
                return new TrafficRecord
                {
                    Timestamp = ts,
                    Pid = pid,
                    ProcessName = GetProcessName(pid),
                    SentBytes = sent - prevSent,
                    RecvBytes = recv - prevRecv
                };
            })
            .Where(r => r.SentBytes != 0 || r.RecvBytes != 0)
            .OrderByDescending(r => r.SentBytes + r.RecvBytes)
            .ToList();

            if (rows.Count > 0)
            {
                foreach (var r in rows)
                {
                    _lastSent[r.Pid] = _sentByPid.TryGetValue(r.Pid, out var s) ? s : 0;
                    _lastRecv[r.Pid] = _recvByPid.TryGetValue(r.Pid, out var rc) ? rc : 0;
                    _writeQueue.Add(r);
                }
                OnSnapshotCaptured?.Invoke(rows);
            }

            long totalSent = Interlocked.Read(ref _totalSent);
            long totalRecv = Interlocked.Read(ref _totalReceived);
            OnLogMessage?.Invoke($"全局累计发/收: {FormatBytes(totalSent)} / {FormatBytes(totalRecv)}");
        }

        private string GetProcessName(int pid)
        {
            if (pid == 0) return "Idle/System";
            if (_nameCache.TryGetValue(pid, out var cached)) return cached;
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(pid))
                {
                    string name = p.ProcessName;
                    _nameCache[pid] = name;
                    return name;
                }
            }
            catch { return "未知"; }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
        }

        private void InitDatabase()
        {
            using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        PRAGMA journal_mode=WAL;
                        CREATE TABLE IF NOT EXISTS TrafficStats (
                            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                            Timestamp   TEXT    NOT NULL,
                            Pid         INTEGER NOT NULL,
                            ProcessName TEXT,
                            SentBytes   INTEGER NOT NULL,
                            RecvBytes   INTEGER NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS idx_traffic_ts  ON TrafficStats(Timestamp);
                        CREATE INDEX IF NOT EXISTS idx_traffic_pid ON TrafficStats(Pid);";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private void DbWriterLoop()
        {
            const int batchSize = 200;
            var batch = new System.Collections.Generic.List<TrafficRecord>(batchSize);

            foreach (var record in _writeQueue.GetConsumingEnumerable())
            {
                batch.Add(record);
                while (batch.Count < batchSize && _writeQueue.TryTake(out var more, 0))
                    batch.Add(more);

                if (batch.Count > 0)
                {
                    try { WriteBatch(batch); }
                    catch (Exception ex) { OnLogMessage?.Invoke($"[DB写入失败] {ex.Message}"); }
                    batch.Clear();
                }
            }
        }

        private void WriteBatch(System.Collections.Generic.List<TrafficRecord> records)
        {
            using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO TrafficStats (Timestamp, Pid, ProcessName, SentBytes, RecvBytes) VALUES ($ts, $pid, $name, $sent, $recv);";
                    var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
                    var pPid = cmd.CreateParameter(); pPid.ParameterName = "$pid"; cmd.Parameters.Add(pPid);
                    var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
                    var pSent = cmd.CreateParameter(); pSent.ParameterName = "$sent"; cmd.Parameters.Add(pSent);
                    var pRecv = cmd.CreateParameter(); pRecv.ParameterName = "$recv"; cmd.Parameters.Add(pRecv);

                    foreach (var r in records)
                    {
                        pTs.Value = r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                        pPid.Value = r.Pid;
                        pName.Value = r.ProcessName ?? "";
                        pSent.Value = r.SentBytes;
                        pRecv.Value = r.RecvBytes;
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
        }

        public void Dispose() => Stop();
    }
}