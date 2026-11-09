using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 代理运行统计（由窗口底部状态栏轮询展示）。
/// 「每次连接/每个请求都成功」这类高频信息不再逐条写进界面日志（降到 DEBUG 只落文件），
/// 实时可见性由这里的计数承担，所以新增高频计数时要同时想清楚它对应哪一段文案。
/// </summary>
public sealed class ProxyStatistics
{
    private long _requestsTotal, _cacheHits, _raceRequests, _tunnels, _mitmConnects, _errors, _edgeConnections;
    private readonly object _winnerLock = new();
    private string? _lastWinner;
    /// <summary>「各域名胜出 IP」明细上限：状态栏只展示前 10，没有理由无限记下去
    /// ——长时间运行几百上千个域名后，整字典拷贝 + 锁内耗时会同时拖垮 UI 轮询和 RecordWinner 热路径。</summary>
    private const int MaxWinners = 512;
    private readonly Dictionary<string, (string Ip, int Count)> _winners = new(StringComparer.OrdinalIgnoreCase);

    public long RequestsTotal => Interlocked.Read(ref _requestsTotal);
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long RaceRequests => Interlocked.Read(ref _raceRequests);
    public long Tunnels => Interlocked.Read(ref _tunnels);
    public long MitmConnects => Interlocked.Read(ref _mitmConnects);
    public long Errors => Interlocked.Read(ref _errors);
    /// <summary>走 CDN 优选边缘 IP 建立成功的连接数（情况1，含缓存命中）。</summary>
    public long EdgeConnections => Interlocked.Read(ref _edgeConnections);

    public string? LastWinner
    {
        get { lock (_winnerLock) return _lastWinner; }
    }

    /// <summary>按胜出次数取前 <paramref name="max"/> 名（锁内直接算好返回，不再整字典拷贝到 UI 线程）。</summary>
    public IReadOnlyList<(string Host, string Ip, int Count)> TopWinners(int max)
    {
        lock (_winnerLock)
        {
            if (_winners.Count == 0) return Array.Empty<(string, string, int)>();
            var list = new List<(string Host, string Ip, int Count)>(_winners.Count);
            foreach (var kv in _winners) list.Add((kv.Key, kv.Value.Ip, kv.Value.Count));
            list.Sort((x, y) => y.Count.CompareTo(x.Count));
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }
    }

    public void AddRequest() => Interlocked.Increment(ref _requestsTotal);
    public void AddCacheHit() => Interlocked.Increment(ref _cacheHits);
    public void AddRace() => Interlocked.Increment(ref _raceRequests);
    public void AddTunnel() => Interlocked.Increment(ref _tunnels);
    public void AddMitm() => Interlocked.Increment(ref _mitmConnects);
    public void AddError() => Interlocked.Increment(ref _errors);
    public void AddEdgeConnection() => Interlocked.Increment(ref _edgeConnections);

    public void RecordWinner(string host, IPAddress ip, int elapsedMs)
    {
        lock (_winnerLock)
        {
            // elapsedMs<=0 表示这条路径没测建连耗时（CDN 优选 / TCP 竞速只报 IP），别让状态栏出现 (0ms)
            _lastWinner = elapsedMs > 0 ? $"{host} → {ip} ({elapsedMs}ms)" : $"{host} → {ip}";
            if (!_winners.ContainsKey(host) && _winners.Count >= MaxWinners)
            {
                // 满了就淘汰次数最少的域名：热点域名次数高不会被挤掉，冷门域名下次赢了自然会回来
                string? coldest = null;
                var min = int.MaxValue;
                foreach (var kv in _winners)
                {
                    if (kv.Value.Count >= min) continue;
                    min = kv.Value.Count;
                    coldest = kv.Key;
                }
                if (coldest is not null) _winners.Remove(coldest);
            }
            var prev = _winners.TryGetValue(host, out var old) ? old.Count : 0;
            _winners[host] = (ip.ToString(), prev + 1);
        }
    }
}

/// <summary>
/// DNS 加速池的解析枢纽。一个域名只有**一次全池扫描**：
/// 第一次访问（或缓存条目过期）时后台开一个 task，把池里所有 DNS **一次性并发**问完，
/// 谁先答就把它的 IP 追加进 <see cref="DnsResultCache"/> 的条目并拨一次「有变化」信号；
/// 扫完（含最后的系统 DNS）就在条目上打 <c>ScanCompleted</c> 标识。
/// 浏览器同一个域名开的那 4~5 条连接共用这个条目：各自拿「本连接还没试过」的 IP 去竞速，
/// 都不会重复扫描，也不会各自维护一份进度；只有「标识已完成 且 没有新 IP 了」才判定失败。
/// 每条连接的第一批还要过一道冷启动聚合窗口（<see cref="CollectFirstBatchAsync"/>），
/// 免得「第一个答话的 DNS 恰好只给出一个不通的 IP」把整条连接拖满一批的失败探测预算。
/// 池（<see cref="_ranked"/>）来自「优选DNS」页的扫描结果，可整体热更新，未扫描时为空＝只走系统 DNS。
/// </summary>
public sealed class ResolverHub : IDisposable
{
    private readonly DnsResultCache _cache;
    private readonly ProxyStatistics _stats;
    /// <summary>正在扫描的域名（同域名只允许一个扫描 task；扫描本身不跟随连接取消，跑完为止）。</summary>
    private readonly ConcurrentDictionary<string, byte> _sweeping = new(StringComparer.OrdinalIgnoreCase);
    private volatile List<DnsSpeedEntry> _ranked = new();

    public int QueryTimeoutMs { get; set; } = 2500;    public TimeSpan CacheMinTtl { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan CacheMaxTtl { get; set; } = TimeSpan.FromSeconds(600);
    public TimeSpan FallbackTtl { get; set; } = TimeSpan.FromSeconds(300);
    /// <summary>一个 IP 都没解析出来时的「已扫完」负缓存时长：太短会让每条新连接都重扫一遍池子，太长会拖住恢复。</summary>
    public TimeSpan NegativeTtl { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// 冷启动首批聚合的目标候选数（见 <see cref="CollectFirstBatchAsync"/>）：这条连接第一批少于这个数就多等一会儿。
    /// 默认 2 —— 只治「池里第一个答话的 DNS 恰好只给出一个候选」那个病态：首批已经有 ≥2 个候选说明 RRset 已经齐了，
    /// 直接发；单 A 记录的域名（白名单里的非 CDN 源站很常见）本来也不会有第二个，等它没有意义。
    /// 1（或 0）＝ 关闭聚合，恢复「解析出一个就立刻发」的旧行为。目前不进界面，代码里调。
    /// </summary>
    public int FirstBatchMinIps { get; set; } = 5;
    /// <summary>冷启动首批聚合窗口的上限（ms，实际生效值 clamp 到 1..2000）：攒不够也就等这么久，首字节延迟有硬顶。</summary>
    public int FirstBatchCollectMs { get; set; } = 200;

    /// <summary>测试/调试用的静态 hosts 映射（优先级最高，常规使用留空）。</summary>
    public IReadOnlyDictionary<string, string> StaticHosts { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public DnsResultCache Cache => _cache;
    public IReadOnlyList<DnsSpeedEntry> RankedServers => _ranked;
    public bool HasScanResults => _ranked.Count > 0;
    /// <summary>本枢纽使用的统计对象；未外部传入时自建，ProxyServer 应复用这里的一份，缓存命中数才不会丢。</summary>
    public ProxyStatistics Stats { get; }

    public ResolverHub(ProxyStatistics? stats = null, DnsResultCache? cache = null)
    {
        Stats = stats ?? new ProxyStatistics();
        _stats = Stats;
        _cache = cache ?? new DnsResultCache();
    }

    /// <summary>直接替换 DNS 排行（用于从 settings.json 恢复上次的加速池）。</summary>
    public void SetRankedServers(IEnumerable<DnsSpeedEntry> ranked)
    {
        _ranked = ranked
            .GroupBy(e => e.Server)
            .Select(g => g.OrderBy(e => e.LatencyMs).First())
            .OrderBy(e => e.LatencyMs)
            .ThenBy(e => e.Server.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>清空 DNS 排行（＝加速池停用，解析全部回退系统 DNS）。</summary>
    public void ClearRankedServers() => _ranked = new List<DnsSpeedEntry>();

    /// <summary>
    /// 扫描完成后应用：DNS 排行以 IP 为中心——逐个 <see cref="IpGroupResult"/> 取组内最快的那个 DNS
    /// （同一 DNS 若是多个 IP 的最快来源则去重，保留最小延迟），再按延迟升序；并预置被扫描域名的 IP 缓存。
    /// 预置的条目只带 IP、没打 <c>ScanCompleted</c>，所以首次解析仍会把池子扫一遍（顺带把标识补齐）。
    /// 返回值即新的排行，供调用方持久化成 DNS 加速池（避免两处各写一遍同样的算法）。
    /// </summary>
    public IReadOnlyList<DnsSpeedEntry> ApplyScan(DnsScanReport report)
    {
        var bestPerIp = new List<DnsSpeedEntry>();
        foreach (var g in report.Groups)
        {
            var fastest = g.Servers.MinBy(static e => e.LatencyMs);
            if (fastest is not null) bestPerIp.Add(fastest);
        }

        _ranked = bestPerIp
            .GroupBy(e => e.Server)
            .Select(grp => grp.OrderBy(e => e.LatencyMs).First())
            .OrderBy(e => e.LatencyMs)
            .ThenBy(e => e.Server.ToString(), StringComparer.Ordinal)
            .ToList();

        if (report.Groups.Count > 0)
        {
            var ips = report.Groups.Select(g => g.Ip).ToList();
            _cache.Set(report.Domain, ips, TimeSpan.FromSeconds(60));
            AppLog.Info($"已应用扫描结果: {report.SucceededServers}/{report.TotalServers} 个 DNS 可用，" +
                        $"域名 {report.Domain} 预置 {ips.Count} 个 IP 缓存");
        }
        AppLog.Info($"DNS 排行已更新: 每 IP 取最快 DNS 共 {_ranked.Count} 个，" +
                    $"最快: {(_ranked.Count > 0 ? _ranked[0].ToString() : "无")}");
        return _ranked;
    }

    // ====================================================================
    //                        全池扫描 + 共享条目
    // ====================================================================

    /// <summary>该域名的 DNS 池是否已全部扫完（条目过期＝没扫完，下次访问重扫）。</summary>
    public bool IsScanCompleted(string host) => _cache.IsScanCompleted(Normalize(host));

    /// <summary>当前已知可用 IP 的个数（条目过期/不存在＝0）。</summary>
    public int KnownIpCount(string host) => _cache.IpCount(Normalize(host));

    /// <summary>
    /// 保证该域名有一次全池扫描在跑：条目还在且已标完成＝什么都不用做；否则最多起一个后台 task。
    /// 扫描不跟随任何连接的取消令牌，一定跑完（每个 DNS 查询自带超时，整轮也就是一次并发往返的量）。
    /// </summary>
    public void EnsureSweep(string host)
    {
        host = Normalize(host);
        if (_cache.IsScanCompleted(host)) return;
        _cache.Touch(host, NegativeTtl);                        // 先有条目，等待者才不会拿到空快照后干转
        if (_sweeping.ContainsKey(host)) return;                 // 已经有 task 在扫这个域名
        if (!_sweeping.TryAdd(host, 0)) return;                  // 与另一个线程抢到了同一个域名
        _ = Task.Run(() => RunSweepAsync(host));
    }

    /// <summary>
    /// 取一批「本连接还没试过」的候选 IP：缓存里有就直接给；扫描还没出 IP 就等「有变化」信号
    /// （每追加一批新 IP、或标记扫完都会被拨一次）。返回 null 表示这个域名再没有可试的 IP 了
    /// ——即 DNS 池（含系统 DNS 兜底）已全部扫完，且列表里的地址本连接都试过了。
    /// 本连接的第一批（<paramref name="tried"/> 为空）会先过一道 <see cref="CollectFirstBatchAsync"/> 聚合窗口。
    /// </summary>
    /// <param name="host">域名。</param>
    /// <param name="tried">本连接已经试过的 IP（只在这条连接内记账，不影响别人，也不动缓存）。</param>
    /// <param name="ct">连接级取消令牌：客户端断开就立刻收工。</param>
    public async Task<IReadOnlyList<IPAddress>?> WaitForNewIpsAsync(
        string host, IReadOnlyCollection<IPAddress> tried, CancellationToken ct = default)
    {
        host = Normalize(host);
        var firstRound = true;

        while (true)
        {
            if (ct.IsCancellationRequested) return null;

            if (firstRound && _cache.IpCount(host) > 0) _stats.AddCacheHit();  // 条目里现成就有 IP＝真正的缓存命中
            firstRound = false;
            EnsureSweep(host);   // 放在循环里：条目被 TTL 清理掉了也能立刻补一次扫描

            if (_cache.TrySnapshot(host, out var ips, out var completed, out var change))
            {
                IReadOnlyList<IPAddress> fresh = PickUntried(ips, tried);
                if (fresh.Count > 0)
                {
                    // tried 为空＝这条连接还没发过竞速，也就是它的第一批：先凑一凑再发
                    if (tried.Count == 0)
                        fresh = await CollectFirstBatchAsync(host, fresh, completed, tried, ct).ConfigureAwait(false);
                    return fresh;
                }

                if (completed) return null;        // 扫完了，本连接也已经把所有 IP 试过
            }

            await WaitChangeAsync(change, ct).ConfigureAwait(false);
        }
    }

    /// <summary>条目快照里「这条连接还没试过」的 IP。已试集合只活在一次竞速的局部记账里，缓存那边从来不知道谁试过。</summary>
    private static List<IPAddress> PickUntried(IReadOnlyList<IPAddress> ips, IReadOnlyCollection<IPAddress> tried)
    {
        var fresh = new List<IPAddress>(Math.Max(ips.Count - tried.Count, 1));
        foreach (var ip in ips)
            if (!tried.Contains(ip)) fresh.Add(ip);
        return fresh;
    }

    /// <summary>
    /// 冷启动首批聚合窗口。全池扫描是「谁先答谁先追加」，所以池里第一个答话的 DNS 只给出 1 个 IP 时，
    /// 这条连接就会拿这唯一候选去发第一批竞速——它要是不通，就得吃满整批的失败探测预算
    /// （黑洞地址＝建连 <c>RaceConnectTimeoutMs</c>；「TCP 通但不回响应头」＝整批 <c>RaceBatchBudgetMs</c>），
    /// 而这期间其余 DNS 早已把别的地址追加进同一个条目，只能干等到下一批才用上。
    /// 这里就在发第一批之前最多再等 <see cref="FirstBatchCollectMs"/> 毫秒，把陆续到达的 IP 一次凑齐：
    /// 攒够 <see cref="FirstBatchMinIps"/> 个 / 扫描已标记完成 / 窗口到期，三者任一到达就立刻收手，
    /// 所以代价是给首字节加了一个有硬顶的固定延迟，而且只加在每条连接的第一批、只有 1 个候选的时候。
    /// 不进这里的情况：扫描已扫完（条目里就是全部答案，多等没有意义）、首批已经 ≥<see cref="FirstBatchMinIps"/> 个
    /// （正常 RRset 一次就到位）、窗口配成 0；中途条目被 TTL 清掉也直接收手——聚合不能把手里已有的候选换没。
    /// </summary>
    private async Task<IReadOnlyList<IPAddress>> CollectFirstBatchAsync(
        string host, IReadOnlyList<IPAddress> first, bool scanCompleted,
        IReadOnlyCollection<IPAddress> tried, CancellationToken ct)
    {
        var batch = first;
        if (scanCompleted || FirstBatchMinIps <= batch.Count || FirstBatchCollectMs <= 0)
            return batch;

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(Math.Clamp(FirstBatchCollectMs, 1, 2000));

        while (batch.Count < FirstBatchMinIps && !window.IsCancellationRequested)
        {
            if (!_cache.TrySnapshot(host, out var ips, out var completed, out var change)) break;

            var fresh = PickUntried(ips, tried);
            if (fresh.Count > batch.Count) batch = fresh;
            // 攒够或扫描已扫完就立刻收手，别拿着够用的候选再空等一个信号（窗口到点才出来＝白等）
            if (completed || batch.Count >= FirstBatchMinIps) break;

            // 只等「有变化」信号，不轮询；窗口到点会让它立刻返回（它自带的 2 秒兜底不会被拖长）
            await WaitChangeAsync(change, window.Token).ConfigureAwait(false);
        }

        if (batch.Count > first.Count)
            AppLog.Debug($"DNS 首批聚合 {host}: {first.Count} → {batch.Count} 个候选 IP" +
                         $"（{Math.Clamp(FirstBatchCollectMs, 1, 2000)}ms 窗口内又收到新答案）");
        return batch;
    }

    /// <summary>现在缓存里该域名已知的全部 IP（条目过期/不存在返回 false）。忽略「本连接试过谁」，重赛用它。</summary>
    public bool TryGetKnownIps(string host, out IReadOnlyList<IPAddress> ips) => _cache.TryGet(Normalize(host), out ips);

    /// <summary>
    /// 竞速全失败后还值不值得再赛一轮：额度内（<paramref name="roundsUsed"/> 未用完）就等
    /// <paramref name="retryDelayMs"/>，然后把「该域名现在已知的全部 IP」原样再发一批；额度用尽或条目已过期返回 null。
    /// 候选 IP 从不因一次失败被拉黑（<see cref="DnsResultCache"/> 压根没有删除单个 IP 的入口），
    /// 重赛只是把同一批地址再并发试一次，治的是「刚才不行、过一会又行」这类瞬时抖动。
    /// </summary>
    public async Task<IReadOnlyList<IPAddress>?> RetryKnownIpsAsync(
        string host, int roundsUsed, int retryRounds, int retryDelayMs, CancellationToken ct = default)
    {
        if (retryRounds <= 0 || roundsUsed >= retryRounds || ct.IsCancellationRequested)
            return null;

        try
        {
            await Task.Delay(Math.Clamp(retryDelayMs, 50, 600_000), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;   // 客户端已经断开，别再拖一条连接等重赛
        }

        return TryGetKnownIps(host, out var ips) && ips.Count > 0 ? ips : null;
    }

    /// <summary>
    /// 等一次「有变化」信号。正常靠扫描追加 IP / 标记扫完来唤醒；
    /// 2 秒的兜底超时只是为了信号可能被 TTL 清理等边界吃掉时不吊住连接（不算轮询开销）。
    /// </summary>
    private static async Task WaitChangeAsync(Task change, CancellationToken ct)
    {
        if (change.IsCompleted) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(TimeSpan.FromSeconds(2), linked.Token);
        await Task.WhenAny(change, timer).ConfigureAwait(false);
        try { linked.Cancel(); } catch { }         // 关掉兜底定时器
        _ = timer.ContinueWith(static t => { var _ = t.Exception; }, TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>
    /// 一次全池扫描：并发问池里每一个 DNS，谁先答就把 IP 追加进共享条目（连接因此不必等整轮扫完），
    /// 最后再补一手系统 DNS，然后无条件打 <c>ScanCompleted</c> 标识——所以「标识完成」＝「所有解析手段都用尽了」。
    /// </summary>
    private async Task RunSweepAsync(string host)
    {
        var started = DateTime.UtcNow;
        try
        {
            _cache.Touch(host, NegativeTtl);

            // IP 字面量 / 测试用静态映射：没有 DNS 可问，直接落条目
            if (IPAddress.TryParse(host, out var literal))
            {
                _cache.Append(host, new[] { literal }, FallbackTtl);
                return;
            }
            if (StaticHosts.TryGetValue(host, out var mapped) && IPAddress.TryParse(mapped, out var staticIp))
            {
                _cache.Append(host, new[] { staticIp }, FallbackTtl);
                return;
            }

            var ranked = _ranked;
            if (ranked.Count > 0)
            {
                // 池子本来就不大，全部并发问；每个查询自带超时，谁先答谁先往共享条目里追加
                var queries = ranked.Select(s => QueryAndAppendAsync(s, host)).ToArray();
                var answered = (await Task.WhenAll(queries)).Count(n => n > 0);
                // 每个域名一次（且 TTL 到期还会再来一次），属于「持续刷屏」的主要来源之一：只写文件。
                AppLog.Debug($"DNS 加速扫描 {host}: 池内 {ranked.Count} 个 DNS 全部并发 → {answered} 个有应答，" +
                            $"共 {_cache.IpCount(host)} 个 IP（{(int)(DateTime.UtcNow - started).TotalMilliseconds}ms）");
            }
            else
            {
                AppLog.Debug($"DNS 加速池未启用，{host} 直接走系统 DNS");
            }

            // 最后一手：系统 DNS。这样「扫完」就等于「加速池 + 系统 DNS 全部用尽」，
            // 非 MITM 路径也不必再回退一次原始 DNS 去解析同一批地址。
            var sys = await SystemDnsResolveAsync(host).ConfigureAwait(false);
            if (sys.Count > 0)
            {
                var added = _cache.Append(host, sys, FallbackTtl);
                if (added > 0) AppLog.Debug($"系统 DNS 兜底解析 {host} → 新增 {added} 个 IP");
            }
            else if (ranked.Count == 0)
            {
                AppLog.Warn($"系统 DNS 也没解析出 {host} 的地址");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"DNS 加速扫描 {host} 异常：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            // 先立标识再摘「正在扫」标记，中间不会有人趁虚再起一个扫描
            _cache.MarkScanCompleted(host, NegativeTtl);
            _sweeping.TryRemove(host, out _);
        }
    }

    /// <summary>问一个 DNS 并把它给出的 IP 立刻追加进共享条目；返回新增个数（失败/无新 IP＝0）。</summary>
    private async Task<int> QueryAndAppendAsync(DnsSpeedEntry server, string host)
    {
        //这里尝试3次查询，如果3次都失败，就直接返回0
        UdpDnsResult? r = null;
        for (int i = 0; i < 3; i++)
        {
            r = await TryQueryAsync(server, host).ConfigureAwait(false);
            if(r is null)
            {
                continue;
            }
            else
            {
                break;
            }
        }
        if(r is null)
        {
            // 池里某个 DNS 不通是常态（每次扫描、每个域名都会再报一遍），所以只落文件；
            // 全军覆没由上面的「一个 IP 都没解析出来」WARN 负责通知。
            AppLog.Debug($"DNS 加速扫描 {host}: {server.Server} 查询失败 3 次");
        }
        if (r is null || r.Ips.Count == 0) return 0;
        
        var filteredIps = r.Ips.Where(ip => !ip.Equals(IPAddress.Loopback) && !ip.Equals(IPAddress.IPv6Loopback)).ToList();//去掉返回解析为本地127.0.0.1的IP。
        var ttl = ClampTtl(TimeSpan.FromSeconds(Math.Max(r.TtlSeconds, 1)));
        var added = _cache.Append(host, filteredIps, ttl);
        if (added > 0) AppLog.Debug($"DNS 加速扫描 {host}: {server.Server} 新增 {added} 个 IP，TTL {ttl.TotalSeconds:0}s");
        return added;
    }

    private async Task<UdpDnsResult?> TryQueryAsync(DnsSpeedEntry server, string host, CancellationToken ct = default)
    {
        try
        {
            // 给较慢的服务器更宽的预算，但整体仍有上限
            //var timeout = Math.Clamp(server.LatencyMs * 3 + 800, 500, Math.Max(QueryTimeoutMs, 2000));
            var timeout = 2000;//直接写死，否则有些DNS返回不了数据。
            return await UdpDnsClient.QueryAAsync(server.Server, host, timeout, ct);
        }
        catch (Exception ex){
            //AppLog.Warn($"DNS 加速扫描 {host}: {server.Server} 查询异常：{ex.GetType().Name} {ex.Message}");
            return null; 
        }
    }

    /// <summary>
    /// 系统 DNS 兜底解析。<paramref name="timeoutMs"/> 时限是硬要求：它是全池扫描的最后一步，
    /// 一旦卡住（VPN 切换、DNS Client 服务异常——恰是本软件的典型环境），扫描永远不完成，
    /// 该域名的每条新连接都会无限等下去＝浏览器超时报代理错误。
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> SystemDnsResolveAsync(string host, int timeoutMs = 5000)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 60_000)));
            var addrs = await System.Net.Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
            var v4 = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            return v4.Length > 0 ? v4 : addrs;
        }
        catch (OperationCanceledException)
        {
            AppLog.Warn($"系统 DNS 解析 {host} 超时，跳过（加速池查询结果不受影响）");
            return Array.Empty<IPAddress>();
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static string Normalize(string host) => host.TrimEnd('.').ToLowerInvariant();

    private TimeSpan ClampTtl(TimeSpan ttl) =>
        ttl < CacheMinTtl ? CacheMinTtl
        : ttl > CacheMaxTtl ? CacheMaxTtl
        : ttl;

    public void Dispose()
    {
        _cache.Dispose();
        _sweeping.Clear();
    }
}
