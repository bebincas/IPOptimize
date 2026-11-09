using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace CloudflareSmartProxy.Services;

public sealed class LogService
{
    public ObservableCollection<string> Items { get; } = new();
    public event EventHandler? ItemAdded;

    private static readonly Logger? _nlogger;
    private static readonly string _logFilePath;

    // ———— UI 批量刷新 ————
    // 原实现每条日志一次 InvokeAsync + 外层再排一次滚动（两个 ListBox 各 ScrollIntoView）：
    // 上游变差时竞速失败的 WARN 会刷到几百条/秒，Dispatcher 队列被压垮＝界面假死，而且后台
    // 还在产日志、越积越多。改成先入队、合并成一次刷新：积压的行在 Dispatcher 里只占一个槽位，
    // Drain 一次性入列表、只触发一次 ItemAdded（＝一次滚动）。
    private const int MaxUiLines = 1000;      // Items 上限（与原实现一致）
    private const int UiQueueLimit = 2000;    // 待刷队列兜底上限：UI 线程长时间不消化时丢最老的行
    private readonly object _uiLock = new();
    private readonly Queue<string> _uiQueue = new();
    private string? _pendingProgress;         // Progress 只关心最新一条，旧的自然作废
    private bool _drainScheduled;

    static LogService()
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudflareSmartProxy", "logs");
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, $"proxy-{DateTime.Now:yyyyMMdd}.log");

            var config = LogManager.Configuration ?? new LoggingConfiguration();
            var fileTarget = new FileTarget("file")
            {
                FileName = _logFilePath,
                Layout = "${longdate} [${level:uppercase=true}] ${message}",
                // 单文件超过 5MB 自动滚动归档，最多保留 10 个历史归档，避免日志无限增长。
                ArchiveAboveSize = 5 * 1024 * 1024,
                MaxArchiveFiles = 10,
                ArchiveNumbering = ArchiveNumberingMode.Rolling
            };
            config.AddTarget(fileTarget);
            config.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, fileTarget);
            LogManager.Configuration = config;
            _nlogger = LogManager.GetCurrentClassLogger();
        }
        catch
        {
            // If NLog fails to initialize, fall back to simple file path in temp
            try
            {
                var logDir = Path.Combine(Path.GetTempPath(), "CloudflareSmartProxy", "logs");
                Directory.CreateDirectory(logDir);
                _logFilePath = Path.Combine(logDir, $"proxy-{DateTime.Now:yyyyMMdd}.log");
            }
            catch
            {
                _logFilePath = Path.Combine(Environment.CurrentDirectory, $"proxy-{DateTime.Now:yyyyMMdd}.log");
            }
            _nlogger = null;
        }
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);
    public void Debug(string message) => Write("DEBUG", message);

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        if (level == "INFO" || level == "ERROR" || level == "WARN")
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null)
                EnqueueUiLine(dispatcher, line);   // null＝关窗/退出阶段：只落文件，别再碰 UI 集合
        }

        // Also write to NLog (which is configured to write to a local file), or fall back to appending to file
        try
        {
            if (_nlogger is not null)
            {
                switch (level)
                {
                    case "INFO": _nlogger.Info(message); break;
                    case "WARN": _nlogger.Warn(message); break;
                    case "ERROR": _nlogger.Error(message); break;
                    case "DEBUG": _nlogger.Debug(message); break;
                    default: _nlogger.Info(message); break;
                }
            }
            else
            {
                // fallback append
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // ignore logging failures
        }
    }

    private void EnqueueUiLine(Dispatcher dispatcher, string line)
    {
        lock (_uiLock)
        {
            while (_uiQueue.Count >= UiQueueLimit) _uiQueue.Dequeue();   // 兜底：别让待刷队列无限涨
            _uiQueue.Enqueue(line);
        }
        if (dispatcher.CheckAccess())
        {
            DrainUi();   // 本来就在 UI 线程（按钮、启动流程）：同步可见
            return;
        }
        ScheduleDrain(dispatcher);
    }

    private void ScheduleDrain(Dispatcher dispatcher)
    {
        lock (_uiLock)
        {
            if (_drainScheduled) return;   // 已有一次刷新在排队：后来的行合并进它
            _drainScheduled = true;
        }
        _ = dispatcher.InvokeAsync(DrainUi);
    }

    /// <summary>把积压的行一次性搬进 Items：一次批量添加 + 一次截断 + 一次 ItemAdded（＝一次滚动）。只在 UI 线程跑。</summary>
    private void DrainUi()
    {
        List<string>? lines = null;
        string? progress = null;
        lock (_uiLock)
        {
            _drainScheduled = false;
            if (_uiQueue.Count > 0)
            {
                lines = new List<string>(_uiQueue.Count);
                while (_uiQueue.Count > 0) lines.Add(_uiQueue.Dequeue());
            }
            progress = _pendingProgress;
            _pendingProgress = null;
        }

        if (lines is not null)
        {
            // UI 列表本来就只留 MaxUiLines 行：积压比这还多时老行直接丢，省掉逐条 Add 的开销
            if (lines.Count > MaxUiLines)
                lines = lines.GetRange(lines.Count - MaxUiLines, MaxUiLines);
            foreach (var line in lines)
                Items.Add(line);
            while (Items.Count > MaxUiLines)
                Items.RemoveAt(0);
        }

        if (progress is not null)
        {
            if (Items.Count == 0) Items.Add(progress);
            else Items[Items.Count - 1] = progress;   // ObservableCollection 索引赋值会触发替换通知
        }

        if (lines is not null || progress is not null)
            ItemAdded?.Invoke(this, EventArgs.Empty);
    }

    // 用于进度条或实时替换最后一行日志（不会无限追加）；高频调用时只保留最新一条待刷。
    public void Progress(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        lock (_uiLock) _pendingProgress = line;
        if (dispatcher.CheckAccess())
        {
            DrainUi();
            return;
        }
        ScheduleDrain(dispatcher);
    }
}
