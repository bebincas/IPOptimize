using CloudflareSmartProxy.Models;
using CloudflareSmartProxy.Services;
using CloudflareSmartProxy.Utilities;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CloudflareSmartProxy;

/// <summary>
/// 「优选DNS」页与 DNS 加速 / MITM 相关的界面装配逻辑，从 <see cref="MainWindow"/> 拆出来放这里，
/// 避免把已经很长的主文件继续撑大。共享设置的读写仍统一走主窗体的
/// TryReadSettings / MainWindow_Loaded / ResetSpeedOptions_Click（这里只提供被它们调用的三个方法）。
/// </summary>
public partial class MainWindow
{
    // ———— 跨代理实例复用的服务：解析缓存与 DNS 池不应随「停止代理」而丢失 ————
    private readonly ResolverHub _dnsResolver = new();
    private readonly ProxyOptions _proxyOptions = new();
    private readonly DnsScanner _dnsScanner = new();
    private CaCertificateService? _ca;
    private CancellationTokenSource? _dnsScanCts;
    private DnsScanReport? _lastScanReport;

    /// <summary>
    /// 最近一次扫描实际用的 DNS 列表（在线/缓存/内嵌 + exe 同目录 dns-servers.txt 合并后的结果）。
    /// 每次扫描都会重取（见 DnsServerList.LoadAsync），这份只是留给「导出 DNS 列表模板」当下用。
    /// </summary>
    private IReadOnlyList<DnsServerEntry>? _dnsServers;

    /// <summary>扫描结果表的数据源（按 IP 分组展示）。</summary>
    public ObservableCollection<DnsScanRow> DnsScanRows { get; } = new();

    /// <summary>DNS 加速池（持久化在 settings.json 里，随 _settings 一起被替换，所以绑定要在 Loaded 后重挂）。</summary>
    public ObservableCollection<DnsPoolEntry> DnsPool => _settings.DnsPool;

    /// <summary>
    /// DNS 相关服务的初始化，由主窗体构造函数在 InitializeComponent 之后调用
    /// （控件要已存在，所以不能写成字段初始化器，也不能另开一个 MainWindow 构造函数）。
    /// </summary>
    private void InitDnsServices()
    {
        // 服务层（DNS/竞速/证书）通过 AppLog 写日志：它不持有 LogService，避免反向依赖 UI。
        AppLog.Sink = (level, message) =>
        {
            switch (level)
            {
                case "WARN": _log.Warn(message); break;
                case "ERROR": _log.Error(message); break;
                case "DEBUG": _log.Debug(message); break;
                default: _log.Info(message); break;
            }
        };

        _dnsResolver.Cache.Expired += OnDnsCacheExpired;   // 让缓存过期清理进日志

        // 扫描结果表用扁平排序（按 IP 组序号、组内延迟升序）。
        // 不用 DataGrid 分组：分组模式下这个表的列宽会被测成最小 20px（表头/单元格全被压扁），
        // 而分组带来的视觉分隔用「解析出的 IP」这一列同样能表达。
        DnsScanGrid.ItemsSource = DnsScanRows;
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(DnsScanRows);
        view.SortDescriptions.Add(new SortDescription(nameof(DnsScanRow.GroupRank), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(DnsScanRow.LatencyMs), ListSortDirection.Ascending));
    }

    private void OnDnsCacheExpired(string domain) =>
        // Debug 级别在 LogService 里只落文件（不碰 UI 集合），没必要再排到 UI 线程去写 NLog
        _log.Debug($"DNS 解析缓存过期清理：{domain}");

    // ====================================================================
    //                        设置项读写（由主窗体统一调用）
    // ====================================================================

    /// <summary>Loaded：把已保存的 DNS/竞速/MITM 参数与加速池回填到界面并恢复到解析枢纽。</summary>
    private void FillDnsOptions(AppSettings s)
    {
        DnsDomainTextBox.Text = string.IsNullOrWhiteSpace(s.DnsLastScanDomain) ? "github.com" : s.DnsLastScanDomain;
        DnsScanConcurrencyTextBox.Text = s.DnsScanConcurrency.ToString();
        DnsScanTimeoutTextBox.Text = s.DnsScanTimeoutMs.ToString();
        DnsAccelEnabledCheckBox.IsChecked = s.DnsAccelEnabled;
        MitmEnabledCheckBox.IsChecked = s.MitmEnabled;
        RaceMaxIpsTextBox.Text = s.RaceMaxIps.ToString();
        DnsTtlMinTextBox.Text = s.DnsCacheMinTtlSec.ToString();
        DnsTtlMaxTextBox.Text = s.DnsCacheMaxTtlSec.ToString();
        RaceBodyLimitTextBox.Text = s.RaceBodyLimitMB.ToString();
        RaceConnectTimeoutTextBox.Text = s.RaceConnectTimeoutMs.ToString();
        RaceBatchBudgetTextBox.Text = s.RaceBatchBudgetMs.ToString();
        MitmConnectTimeoutTextBox.Text = s.MitmConnectTimeoutMs.ToString();
        MitmPortsTextBox.Text = s.MitmPorts;
        RaceRetryRoundsTextBox.Text = s.RaceRetryRounds.ToString();
        RaceRetryDelayTextBox.Text = s.RaceRetryDelayMs.ToString();

        // 加速池列表重新绑定：_settings 被整体替换后 DnsPool 是另一个集合实例，
        // 不重挂会一直显示上一次的空集合（与 ProxyList 同样的默认视图按实例缓存问题）。
        DnsPoolList.ItemsSource = null;
        DnsPoolList.ItemsSource = s.DnsPool;
        ApplyDnsPoolToResolver(s);
        RefreshDnsPoolUi();
    }

    /// <summary>把池灌进解析枢纽（代理未启动时也要灌，启动时直接沿用）。</summary>
    private void ApplyDnsPoolToResolver(AppSettings s)
    {
        _dnsResolver.SetRankedServers(s.DnsPool.Select(p => p.ToSpeedEntry()));
        _dnsResolver.CacheMinTtl = TimeSpan.FromSeconds(Math.Max(1, s.DnsCacheMinTtlSec));
        _dnsResolver.CacheMaxTtl = TimeSpan.FromSeconds(Math.Max(_dnsResolver.CacheMinTtl.TotalSeconds, s.DnsCacheMaxTtlSec));
        _proxyOptions.RaceMaxIps = Math.Clamp(s.RaceMaxIps, 1, 64);
        _proxyOptions.RequestBodyBufferLimitBytes = Math.Max(1, s.RaceBodyLimitMB) * 1024 * 1024;
        _proxyOptions.RaceConnectTimeoutMs = Math.Clamp(s.RaceConnectTimeoutMs, 200, 30000);
        _proxyOptions.RaceBatchBudgetMs = Math.Clamp(s.RaceBatchBudgetMs, 1000, 120000);
        _proxyOptions.MitmConnectTimeoutMs = Math.Clamp(s.MitmConnectTimeoutMs, 200, 30000);
        _proxyOptions.RaceRetryRounds = Math.Clamp(s.RaceRetryRounds, 0, 5);
        _proxyOptions.RaceRetryDelayMs = Math.Clamp(s.RaceRetryDelayMs, 100, 60000);

        // MITM 端口闸门整体替换：读取侧每次都现取引用，所以热更新是安全的；
        // 一个合法端口都解析不出来时保持原值，别把闸门静默清成「哪个端口都不解密」。
        var ports = ParseMitmPorts(s.MitmPorts);
        if (ports.Count > 0)
            _proxyOptions.MitmPorts = ports;
    }

    /// <summary>解析「MITM 解密端口」文本：逗号/分号/空格/换行分隔，取 1～65535 的整数并去重；非法项忽略。</summary>
    private static HashSet<int> ParseMitmPorts(string? text)
    {
        var ports = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(text))
            return ports;

        foreach (var part in text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part.Trim(), out var port) && port is >= 1 and <= 65535)
                ports.Add(port);

        return ports;
    }

    /// <summary>
    /// TryReadSettings 调用：校验并写入 DNS/竞速/MITM 相关设置。
    /// 语义与其它高级测速选项一致——非法文本沿用旧值，越界则弹提示并返回 false 中止保存。
    /// </summary>
    private bool TryReadDnsOptions(AppSettings target)
    {
        if (!TryReadIntOption(DnsScanConcurrencyTextBox.Text, "DNS 扫描并发数", 1, 1024, _settings.DnsScanConcurrency, out target.DnsScanConcurrency))
            return false;
        if (!TryReadIntOption(DnsScanTimeoutTextBox.Text, "DNS 查询超时(ms)", 200, 15000, _settings.DnsScanTimeoutMs, out target.DnsScanTimeoutMs))
            return false;
        if (!TryReadIntOption(RaceMaxIpsTextBox.Text, "竞速每批并发 IP 数", 1, 64, _settings.RaceMaxIps, out target.RaceMaxIps))
            return false;
        if (!TryReadIntOption(DnsTtlMinTextBox.Text, "解析缓存 TTL 下限(秒)", 10, 3600, _settings.DnsCacheMinTtlSec, out target.DnsCacheMinTtlSec))
            return false;
        if (!TryReadIntOption(DnsTtlMaxTextBox.Text, "解析缓存 TTL 上限(秒)", 10, 86400, _settings.DnsCacheMaxTtlSec, out target.DnsCacheMaxTtlSec))
            return false;
        if (target.DnsCacheMaxTtlSec < target.DnsCacheMinTtlSec)
        {
            MessageBox.Show("解析缓存 TTL 上限必须不小于下限。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!TryReadIntOption(RaceBodyLimitTextBox.Text, "请求体缓冲上限(MB)", 1, 256, _settings.RaceBodyLimitMB, out target.RaceBodyLimitMB))
            return false;
        if (!TryReadIntOption(RaceConnectTimeoutTextBox.Text, "竞速建连超时(ms)", 200, 30000, _settings.RaceConnectTimeoutMs, out target.RaceConnectTimeoutMs))
            return false;
        if (!TryReadIntOption(RaceBatchBudgetTextBox.Text, "每批竞速预算(ms)", 1000, 120000, _settings.RaceBatchBudgetMs, out target.RaceBatchBudgetMs))
            return false;
        if (!TryReadIntOption(MitmConnectTimeoutTextBox.Text, "MITM 建连超时(ms)", 200, 30000, _settings.MitmConnectTimeoutMs, out target.MitmConnectTimeoutMs))
            return false;
        if (!TryReadIntOption(RaceRetryRoundsTextBox.Text, "重赛轮数", 0, 5, _settings.RaceRetryRounds, out target.RaceRetryRounds))
            return false;
        if (!TryReadIntOption(RaceRetryDelayTextBox.Text, "重赛间隔(ms)", 100, 60000, _settings.RaceRetryDelayMs, out target.RaceRetryDelayMs))
            return false;

        // MITM 解密端口要至少一个合法值：留空＝所有端口都不解密，这种「静默关掉 MITM」必须挡住，
        // 所以这里不套用 TryReadIntOption 的「文本非法就沿用旧值」语义，直接弹框中止整次保存。
        var ports = ParseMitmPorts(MitmPortsTextBox.Text);
        if (ports.Count == 0)
        {
            MessageBox.Show("MITM 解密端口至少要有一个 1～65535 的整数，多个用逗号分隔，例如 443,8443。",
                "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        target.MitmPorts = string.Join(",", ports.OrderBy(p => p));

        target.DnsAccelEnabled = DnsAccelEnabledCheckBox.IsChecked ?? _settings.DnsAccelEnabled;
        target.MitmEnabled = MitmEnabledCheckBox.IsChecked ?? _settings.MitmEnabled;
        return true;
    }

    /// <summary>「重置为默认」调用。</summary>
    private void ResetDnsOptions(AppSettings def)
    {
        DnsScanConcurrencyTextBox.Text = def.DnsScanConcurrency.ToString();
        DnsScanTimeoutTextBox.Text = def.DnsScanTimeoutMs.ToString();
        DnsAccelEnabledCheckBox.IsChecked = def.DnsAccelEnabled;
        MitmEnabledCheckBox.IsChecked = def.MitmEnabled;
        RaceMaxIpsTextBox.Text = def.RaceMaxIps.ToString();
        DnsTtlMinTextBox.Text = def.DnsCacheMinTtlSec.ToString();
        DnsTtlMaxTextBox.Text = def.DnsCacheMaxTtlSec.ToString();
        RaceBodyLimitTextBox.Text = def.RaceBodyLimitMB.ToString();
        RaceConnectTimeoutTextBox.Text = def.RaceConnectTimeoutMs.ToString();
        RaceBatchBudgetTextBox.Text = def.RaceBatchBudgetMs.ToString();
        MitmConnectTimeoutTextBox.Text = def.MitmConnectTimeoutMs.ToString();
        MitmPortsTextBox.Text = def.MitmPorts;
        RaceRetryRoundsTextBox.Text = def.RaceRetryRounds.ToString();
        RaceRetryDelayTextBox.Text = def.RaceRetryDelayMs.ToString();
    }

    /// <summary>把当前设置热更新到运行中的代理（保存设置 / 应用池 / 启动代理后统一走这里）。</summary>
    private void ApplyDnsSettingsToProxy() => ApplyDnsSettingsToProxy(_settings.DnsAccelEnabled, _settings.MitmEnabled);

    /// <summary>
    /// 按「本次运行」的开关下发 DNS 加速 / MITM：「启动代理」旁的「DNS优选」勾选与根证书询问的结果走这里。
    /// 只影响当前代理实例，不改 settings.json 里的 <c>DnsAccelEnabled</c> / <c>MitmEnabled</c>（下次启动仍按持久化值）。
    /// </summary>
    private void ApplyDnsSettingsToProxy(bool dnsAccelEnabled, bool mitmEnabled)
    {
        ApplyDnsPoolToResolver(_settings);
        var proxy = _proxy;
        if (proxy is null)
            return;
        proxy.UpdateGlobalDnsWhitelist(_settings.GlobalDnsWhitelist);
        proxy.UpdateDnsPool(
            _settings.DnsPool,
            _settings.RaceMaxIps,
            _settings.DnsCacheMinTtlSec,
            _settings.DnsCacheMaxTtlSec,
            _settings.RaceBodyLimitMB,
            dnsAccelEnabled,
            mitmEnabled);
    }

    // ====================================================================
    //                          DNS 加速池界面
    // ====================================================================

    private void RefreshDnsPoolUi()
    {
        var pool = _settings.DnsPool;
        string fastest = pool.Count > 0 ? $"，最快 {pool[0].LatencyMs}ms（{pool[0].Display}）" : "";
        string updated = _settings.DnsPoolUpdatedAt == default ? "从未更新" : _settings.DnsPoolUpdatedAt.ToString("MM-dd HH:mm");
        var summary = pool.Count == 0
            ? $"DNS 加速池：空（白名单域名将回退系统 DNS）｜{updated}"
            : $"DNS 加速池：{pool.Count} 个 DNS（同域名一次并发扫完）｜竞速每批 {_settings.RaceMaxIps} 个 IP{fastest}｜{updated}";
        DnsPoolStatusText.Text = summary;

        // 「开始优化」左列只有 250 宽，整句塞进 Expander 标题会被截成几个字，
        // 所以那里拆成短标题 + 小字副行两截（信息量不变，只是换行放）。
        DnsPoolSummaryText.Text = pool.Count == 0 ? "DNS 加速池：空" : $"DNS 加速池：{pool.Count} 个 DNS";
        DnsPoolSummarySubText.Text = pool.Count == 0
            ? $"白名单域名将回退系统 DNS｜{updated}"
            : $"最快 {pool[0].LatencyMs}ms（{pool[0].Display}）｜竞速每批 {_settings.RaceMaxIps} 个｜{updated}";
    }

    // ====================================================================
    //                              DNS 扫描
    // ====================================================================

    private async void DnsScanButton_Click(object sender, RoutedEventArgs e)
    {
        var domain = DnsDomainTextBox.Text.Trim();
        if (domain.Length == 0)
        {
            MessageBox.Show("请输入要扫描的目标域名。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(DnsScanConcurrencyTextBox.Text, out var concurrency) || concurrency is < 1 or > 1024)
        {
            MessageBox.Show("扫描并发数必须是 1～1024。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(DnsScanTimeoutTextBox.Text, out var timeoutMs) || timeoutMs is < 200 or > 15000)
        {
            MessageBox.Show("DNS 查询超时必须是 200～15000ms。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DnsScanButton.IsEnabled = false;
        DnsScanStopButton.IsEnabled = true;
        DnsApplyPoolButton.IsEnabled = false;
        DnsScanRows.Clear();
        DnsScanProgressBar.Value = 0;
        DnsScanStatusText.Text = "正在加载 DNS 列表（本地缓存优先，过期才联网）…";
        DnsScanSummaryText.Text = "";
        _lastScanReport = null;

        // 记住本次目标域名（下次启动回填），与扫描参数一起落盘。
        _settings.DnsLastScanDomain = domain;
        _settings.DnsScanConcurrency = concurrency;
        _settings.DnsScanTimeoutMs = timeoutMs;
        await _settingsStore.SaveAsync(_settings);

        try
        {
            // 令牌先建好再取列表：「停止」得能打断正在联网下载 DNS 列表的那 20 秒。
            _dnsScanCts = new CancellationTokenSource();
            // 每次扫描都重新取一遍列表：缓存新鲜时只是读个文件（顺带认 exe 同目录的 dns-servers.txt），
            // 过期才联网；解析与文件 IO 放后台线程，~1200 条 JSON 不在 UI 线程上啃。
            var loaded = await Task.Run(() => DnsServerList.LoadAsync(cancellationToken: _dnsScanCts.Token));
            _dnsServers = loaded.Servers;
            DnsScanStatusText.Text = $"开始扫描 {domain}（{loaded.Count} 个 DNS，来源：{loaded.Origin}，并发 {concurrency}）…";

            var progress = new Progress<DnsScanProgress>(p =>
            {
                DnsScanProgressBar.Value = p.Total == 0 ? 0 : p.Completed * 100.0 / p.Total;
                DnsScanStatusText.Text = $"已完成 {p.Completed}/{p.Total}，成功 {p.Succeeded}";
            });

            var sw = Stopwatch.StartNew();
            var report = await Task.Run(() => _dnsScanner.ScanAsync(
                domain, loaded.Servers, concurrency, timeoutMs, progress, _dnsScanCts.Token), CancellationToken.None);

            _lastScanReport = report;
            PopulateScanRows(report);
            DnsScanSummaryText.Text =
                $"耗时 {sw.Elapsed.TotalSeconds:0.0}s｜DNS 可用 {report.SucceededServers}/{report.TotalServers}｜" +
                $"独立 IP {report.Groups.Count} 个｜最快 {report.RankedServers.FirstOrDefault()?.ToString() ?? "无"}";
            DnsApplyPoolButton.IsEnabled = report.RankedServers.Count > 0;
            DnsExportReportButton.IsEnabled = report.RankedServers.Count > 0;
            _log.Info($"DNS 扫描完成：{domain} → {report.SucceededServers}/{report.TotalServers} 个 DNS 可用，解析出 {report.Groups.Count} 个独立 IP");
            this.DnsApplyPool(report);//应用
        }
        catch (OperationCanceledException)
        {
            DnsScanStatusText.Text = "已停止";
            _log.Warn("DNS 扫描已手动停止");
        }
        catch (Exception ex)
        {
            DnsScanStatusText.Text = "扫描失败";
            _log.Error($"DNS 扫描失败：{ex.Message}");
        }
        finally
        {
            _dnsScanCts?.Dispose();
            _dnsScanCts = null;
            DnsScanButton.IsEnabled = true;
            DnsScanStopButton.IsEnabled = false;
        }
    }

    private void DnsScanStopButton_Click(object sender, RoutedEventArgs e) => _dnsScanCts?.Cancel();

    private void PopulateScanRows(DnsScanReport report)
    {
        DnsScanRows.Clear();
        var rank = 0;
        foreach (var g in report.Groups)
        {
            rank++;
            foreach (var s in g.Servers)
            {
                DnsScanRows.Add(new DnsScanRow
                {
                    Ip = g.Ip.ToString(),
                    DnsServer = s.Server.ToString(),
                    DnsName = s.Name ?? "—",
                    LatencyMs = s.LatencyMs,
                    GroupRank = rank,
                    GroupCount = g.Servers.Count,
                });
            }
        }
    }

    private async void DnsApplyPoolButton_Click(object sender, RoutedEventArgs e)
    {
        var report = _lastScanReport;
        if (report is null)
            return;

        this.DnsApplyPool(report);
    }
    /// <summary>
    /// 应用Dns扫描结果到优选DNS中。
    /// </summary>
    /// <param name="report"></param>
    private async void DnsApplyPool(DnsScanReport report)
    {
        // 与解析枢纽用同一套算法（每 IP 取最快 DNS → 去重 → 按延迟升序），避免两处实现漂移。
        var ranked = _dnsResolver.ApplyScan(report);
        _settings.DnsPool = new ObservableCollection<DnsPoolEntry>(ranked.Select(DnsPoolEntry.From));
        _settings.DnsPoolDomain = report.Domain;
        _settings.DnsPoolUpdatedAt = DateTime.Now;
        await _settingsStore.SaveAsync(_settings);

        DnsPoolList.ItemsSource = null;
        DnsPoolList.ItemsSource = _settings.DnsPool;
        ApplyDnsSettingsToProxy();
        RefreshDnsPoolUi();
        EnsureMitmReady();

        _log.Info($"已应用到 DNS 加速池：{ranked.Count} 个 DNS（来源域名 {report.Domain}）");
        Toast($"DNS 加速池已更新：{ranked.Count} 个 DNS。", ToastNotifier.Kind.Success);
    }

    private async void DnsClearPoolButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.DnsPool = new ObservableCollection<DnsPoolEntry>();
        _settings.DnsPoolDomain = string.Empty;
        _settings.DnsPoolUpdatedAt = default;
        _dnsResolver.ClearRankedServers();
        _dnsResolver.Cache.Clear();
        await _settingsStore.SaveAsync(_settings);

        DnsPoolList.ItemsSource = null;
        DnsPoolList.ItemsSource = _settings.DnsPool;
        ApplyDnsSettingsToProxy();
        RefreshDnsPoolUi();
        _log.Warn("DNS 加速池已清空，白名单域名将回退系统 DNS 解析。");
        Toast("DNS 加速池已清空。", ToastNotifier.Kind.Warn);
    }

    private void DnsExportReportButton_Click(object sender, RoutedEventArgs e)
    {
        var report = _lastScanReport;
        if (report is null)
            return;
        var dlg = new SaveFileDialog
        {
            Filter = "文本文件|*.txt",
            FileName = $"dns-scan-{report.Domain}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("CloudflareSmartProxy DNS 扫描报告");
        sb.AppendLine($"域名: {report.Domain}");
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"DNS 服务器: {report.TotalServers} 个，成功 {report.SucceededServers} 个");
        sb.AppendLine($"独立 IP: {report.Groups.Count} 个");
        sb.AppendLine();
        sb.AppendLine("===== IP → 可解析出该 IP 的 DNS 列表（组内按延迟升序） =====");
        var rank = 0;
        foreach (var g in report.Groups)
        {
            rank++;
            sb.AppendLine($"#{rank} {g.Ip}  (最快 {g.FastestLatencyMs}ms，共 {g.Servers.Count} 个 DNS)");
            foreach (var s in g.Servers)
                sb.AppendLine($"    {s.Server,-18} {s.LatencyMs,5}ms  {s.Name}");
        }
        sb.AppendLine();
        sb.AppendLine("===== DNS 响应速度排行 =====");
        foreach (var s in report.RankedServers)
            sb.AppendLine($"{s.Server,-18} {s.LatencyMs,5}ms  {s.Name}");

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString());
            _log.Info($"扫描结果已导出：{dlg.FileName}");
        }
        catch (Exception ex)
        {
            _log.Error($"导出扫描结果失败：{ex.Message}");
        }
    }

    private void DnsExportListButton_Click(object sender, RoutedEventArgs e)
    {
        var servers = _dnsServers;
        if (servers is null)
        {
            MessageBox.Show("请先执行一次扫描（DNS 列表在首次扫描时加载）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = "文本文件|*.txt",
            FileName = DnsServerList.CustomFileName,
        };
        if (dlg.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(dlg.FileName,
                "# CloudflareSmartProxy 自定义 DNS 列表（每行：IP 名称，# 开头为注释）\r\n" +
                DnsServerList.ExportAsText(servers) + "\r\n");
            _log.Info($"DNS 列表模板已导出：{dlg.FileName}（编辑后放回程序目录即可自动合并加载）");
        }
        catch (Exception ex)
        {
            _log.Error($"导出 DNS 列表失败：{ex.Message}");
        }
    }

    // ====================================================================
    //                        根证书（MITM 所需）
    // ====================================================================

    /// <summary>懒建根 CA；持久化目录与 settings.json 同目录（%LOCALAPPDATA%\CloudflareSmartProxy）。</summary>
    private CaCertificateService EnsureCa() => _ca ??= new CaCertificateService(SettingsStore.DirectoryPath);

    /// <summary>
    /// 只「检查」根证书状态并刷新界面文案，<b>不做任何安装</b>——启动时用它，避免一开机就弹 Windows 的信任确认框。
    /// 返回「是否已被当前用户或本机信任」。要装的时候由用户点「设置」页按钮，或启动代理时按提示调 <see cref="InstallMitmCert"/>。
    /// </summary>
    private bool EnsureMitmReady()
    {
        try
        {
            if (!_settings.MitmEnabled)
            {
                UpdateCertStatusUi("未启用解密（MITM）：白名单 HTTPS 仍走透明隧道，只做 DNS 加速与 TCP 竞速。");
                return false;
            }

            var ca = EnsureCa();
            if (CertificateInstaller.IsInstalledAnywhere(ca.Root))
            {
                UpdateCertStatusUi($"已信任（{(CertificateInstaller.IsInstalled(ca.Root, StoreLocation.LocalMachine) ? "本机/全用户" : "当前用户")}）· 指纹 {ca.Root.Thumbprint[..8]}…");
                return true;
            }

            UpdateCertStatusUi("未安装：启用解密后白名单 HTTPS 会握手失败。可在下方「安装到当前用户」安装，或启动代理时按提示安装。");
            return false;
        }
        catch (Exception ex)
        {
            UpdateCertStatusUi("根证书状态检查失败：" + ex.Message);
            _log.Warn($"根证书状态检查异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 安装根证书到 <c>CurrentUser\Root</c>（无需管理员，但 Windows 会弹一次「是否信任此根证书」的确认框）。
    /// 只在用户明确要求时调用（「设置」页按钮 / 启动代理时的询问），<b>不要</b>在程序启动时自动调它。
    /// </summary>
    /// <returns>安装后是否已被信任（用户在 Windows 确认框里点「否」则为 false）。</returns>
    private bool InstallMitmCert()
    {
        try
        {
            var ca = EnsureCa();
            _log.Info("正在安装 MITM 根证书到「当前用户受信任的根」（Windows 可能弹一次确认框）…");
            if (CertificateInstaller.TryInstall(ca.Root, StoreLocation.CurrentUser))
            {
                _log.Info("根证书已安装到 当前用户\\Root（Chrome/Edge 走系统信任链）。");
                UpdateCertStatusUi($"已信任（当前用户）· 指纹 {ca.Root.Thumbprint[..8]}…");
                return true;
            }

            UpdateCertStatusUi("安装未完成（用户取消确认框或权限不足）。");
            _log.Warn("根证书安装未完成（用户取消确认框或权限不足）。");
            return CertificateInstaller.IsInstalledAnywhere(ca.Root);
        }
        catch (Exception ex)
        {
            UpdateCertStatusUi("根证书安装异常：" + ex.Message);
            _log.Warn($"根证书安装异常：{ex.Message}");
            return false;
        }
    }

    private void UpdateCertStatusUi(string state)
    {
        var ca = _ca;
        CertStatusText.Text = ca is null
            ? state
            : $"根证书 {CaCertificateService.CaSubject}\n指纹 {ca.Root.Thumbprint[..8]}…｜{state}";
    }

    private void CertInstallUser_Click(object sender, RoutedEventArgs e) => InstallMitmCert();

    private void CertInstallMachine_Click(object sender, RoutedEventArgs e)
    {
        // LocalMachine\Root 需要管理员：以 runas 重启自身执行安装，主程序本身仍保持 asInvoker。
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            MessageBox.Show("无法定位当前程序路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = CertificateInstaller.InstallCaArgument,
                Verb = "runas",
                UseShellExecute = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(30000);
            if (proc is { ExitCode: 0 })
                _log.Info("根证书已安装到 本机\\Root（全用户信任）。");
            else
                _log.Warn("本机根证书安装未完成（用户取消 UAC 或安装失败）。");
        }
        catch (Exception ex)
        {
            _log.Warn($"管理员安装被取消或失败：{ex.Message}");
        }
        EnsureMitmReady();
    }

    private void CertUninstall_Click(object sender, RoutedEventArgs e)
    {
        var ca = EnsureCa();
        CertificateInstaller.TryUninstall(ca.Root, StoreLocation.CurrentUser);
        CertificateInstaller.TryUninstallBySubject(CaCertificateService.CaSubject);
        UpdateCertStatusUi("已卸载。浏览器若仍在使用，请重启浏览器。");
        _log.Warn("根证书已从受信任的根存储移除，白名单 HTTPS 解密将不再受浏览器信任。");
    }

    private void CertExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "证书|*.cer",
            FileName = "CloudflareSmartProxy-RootCA.cer",
        };
        if (dlg.ShowDialog(this) != true)
            return;
        try
        {
            EnsureCa().ExportPublicCertificate(dlg.FileName);
            _log.Info($"根证书公钥已导出：{dlg.FileName}（Firefox 用自己的证书库，需手动导入）");
        }
        catch (Exception ex)
        {
            _log.Error($"导出证书失败：{ex.Message}");
        }
    }

    /// <summary>提权子进程入口：--install-ca / --uninstall-ca 只操作证书存储后立即退出，不创建主窗口。</summary>
    public static int HandleCertificateArguments(string[] args)
    {
        if (!args.Contains(CertificateInstaller.InstallCaArgument, StringComparer.OrdinalIgnoreCase) &&
            !args.Contains(CertificateInstaller.UninstallCaArgument, StringComparer.OrdinalIgnoreCase))
            return int.MinValue; // 无证书参数，正常启动

        try
        {
            var ca = new CaCertificateService(SettingsStore.DirectoryPath);
            if (args.Contains(CertificateInstaller.InstallCaArgument, StringComparer.OrdinalIgnoreCase))
                return CertificateInstaller.TryInstall(ca.Root, StoreLocation.LocalMachine) ? 0 : 1;

            CertificateInstaller.TryUninstallBySubject(CaCertificateService.CaSubject);
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}
