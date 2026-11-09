using CloudflareSmartProxy.Models;
using CloudflareSmartProxy.Utilities;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 上游确实连不上：DNS 加速池（含最后的系统 DNS）已全部扫完，且解析出的每个候选 IP 都试过、全部失败。
/// 这时再回退一次原始 DNS 只会解析出同一批地址、把浏览器多吊几秒，所以由 CONNECT / 绝对形式 HTTP
/// 分支捕获后直接给客户端回 502。
/// </summary>
public sealed class UpstreamUnavailableException : Exception
{
    public UpstreamUnavailableException(string message) : base(message) { }
}

public sealed class ProxyServer
{
    private static readonly HashSet<int> CloudflareHttpsPorts = new() { 443, 2053, 2083, 2087, 2096, 8443 };
    private static readonly HashSet<int> CloudflareHttpPorts = new() { 80, 8080, 8880, 2052, 2082, 2086, 2095 };
    private const string CloudflareCdn = "Cloudflare";
    /// <summary>情况2 白名单（「设置」页维护，可随时热更新）：只有命中的 host 才走 DomainResolve 解析加速；空＝不启用。</summary>
    private volatile DomainWildcardList? _globalDnsWhitelist;
    /// <summary>全部 CDN 档位网段来源，顺序即归属判定优先级（Cloudflare 最先，保持原分流行为）。</summary>
    private readonly IReadOnlyList<CdnRangeProvider> _providers;
    private readonly LogService _log;
    // 由 StartAsync 启动的后台扫描 SweepCaches 定期清理这两个缓存中已过期的条目，并做容量上限裁剪。
    /// <summary>host → 归属 CDN（null＝不属于任何已接入 CDN）；TTL 沿用原 Cloudflare 判定：命中 60 分钟 / 未命中 15 分钟。</summary>
    private readonly ConcurrentDictionary<string, (string? Cdn, DateTime ExpiresUtc)> _hostCache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// 缓存每个域名的最佳多个 Edge IP，避免每次都去测试所有 IP。
    /// 键为「CDN|host」，各 CDN 档位的缓存互相隔离，不会跨池命中。
    /// </summary>
    private readonly ConcurrentDictionary<string, (IPAddress[] IPs, DateTime ExpiresUtc)> _bestEdgeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    /// <summary>按 CDN 归属分池的优选 IP（空归属按 Cloudflare 处理，兼容旧数据）。</summary>
    private volatile Dictionary<string, IReadOnlyList<IPAddress>> _edgePools;
    // ———— 情况2（DNS 加速）依赖：解析枢纽 / 竞速器 / 参数 / 统计，均由 MainWindow 创建并跨代理实例复用 ————
    /// <summary>DNS 加速池与解析缓存（白名单域名用它解析，非白名单仍走系统 DNS）。</summary>
    private readonly ResolverHub _resolver;
    private readonly UpstreamRacer _racer;
    private readonly ProxyOptions _options;
    private readonly ProxyStatistics _stats;
    /// <summary>MITM 根 CA；null＝本实例不能解密（未初始化或用户未启用）。</summary>
    private readonly CaCertificateService? _ca;
    /// <summary>情况2 总开关：白名单域名是否启用 DNS 加速池解析 + 竞速。</summary>
    private volatile bool _dnsAccelEnabled;
    /// <summary>是否对白名单域名的 HTTPS 做 MITM 解密（关闭时只做 TCP 竞速 + 透明隧道）。</summary>
    private volatile bool _mitmEnabled;
    private TcpListener? _listener;
    /// <summary>当前监听端口（0＝未启动）。唤醒后重建监听 socket 用（见 <see cref="TryRebindListener"/>）。</summary>
    private int _port;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private int _connectionId;
    private Timer? _cacheSweepTimer;
    private static readonly TimeSpan CacheSweepInterval = TimeSpan.FromMinutes(5);
    private const int MaxCacheEntries = 4096;
    /// <summary>明文/隧道连接上「等下一个请求头」的空闲上限：超过就关掉这条 keep-alive 连接，避免空转的任务堆积。</summary>
    private static readonly TimeSpan KeepAliveIdleTimeout = TimeSpan.FromSeconds(60);
    /// <summary>
    /// 未解密 CONNECT 隧道的「双向空闲」上限：任一方向有字节就把计时推后，双向 60 秒无流量才收掉这条隧道。
    /// 原来是「建立后 60 秒」总时长硬掐——长下载/WebSocket 恰好在 1 分钟处被切断，浏览器侧表现成代理报错。
    /// </summary>
    private static readonly TimeSpan TunnelIdleTimeout = TimeSpan.FromSeconds(60);
    /// <summary>CDN 归属判定里系统 DNS 解析的时限：系统解析没有自己的超时，卡住时每个新域名都会被吊死在分类上。</summary>
    private static readonly TimeSpan ClassifyDnsTimeout = TimeSpan.FromSeconds(5);

    public ProxyServer(
        IReadOnlyList<CdnRangeProvider> providers,
        LogService log,
        IEnumerable<ProxyEndpoint> preferredEdges,
        ResolverHub? dnsResolver = null,
        ProxyStatistics? stats = null,
        CaCertificateService? ca = null,
        ProxyOptions? options = null)
    {
        _providers = providers;
        _log = log;
        _edgePools = BuildEdgePools(preferredEdges);
        _options = options ?? new ProxyOptions();
        _stats = stats ?? new ProxyStatistics();
        // 解析枢纽由外部传入时可在代理重启后继续保留 DNS 池与解析缓存；未传则自建（便于测试）。
        _resolver = dnsResolver ?? new ResolverHub(_stats);
        _ca = ca;
        _racer = new UpstreamRacer { Options = _options, Stats = _stats };
    }

    /// <summary>运行统计（DNS 加速页状态栏用）。</summary>
    public ProxyStatistics Statistics => _stats;
    /// <summary>DNS 解析枢纽（加速页展示缓存/排行用）。</summary>
    public ResolverHub Resolver => _resolver;

    public void UpdatePreferredEdges(IEnumerable<ProxyEndpoint> edges) => _edgePools = BuildEdgePools(edges);

    /// <summary>热更新情况2 的 GlobalDns 加速白名单（代理启动与「设置」保存时调用）；null/空白＝关闭全局解析加速。</summary>
    public void UpdateGlobalDnsWhitelist(string? raw) => _globalDnsWhitelist = DomainWildcardList.Parse(raw);

    /// <summary>
    /// 热更新 DNS 加速池与相关参数（扫描应用、启动恢复、「设置」保存时调用）。
    /// pool 为空＝加速池停用，白名单域名解析自动回退系统 DNS。
    /// </summary>
    public void UpdateDnsPool(
        IEnumerable<Models.DnsPoolEntry>? pool,
        int raceMaxIps,
        int cacheMinTtlSec,
        int cacheMaxTtlSec,
        int bodyLimitMB,
        bool dnsAccelEnabled,
        bool mitmEnabled)
    {
        var entries = pool is null
            ? Array.Empty<Models.DnsPoolEntry>()
            : pool.ToArray();

        _resolver.SetRankedServers(entries.Select(e => e.ToSpeedEntry()));
        _resolver.CacheMinTtl = TimeSpan.FromSeconds(Math.Max(1, cacheMinTtlSec));
        _resolver.CacheMaxTtl = TimeSpan.FromSeconds(Math.Max(_resolver.CacheMinTtl.TotalSeconds, cacheMaxTtlSec));
        _options.RaceMaxIps = Math.Clamp(raceMaxIps, 1, 64);
        _options.RequestBodyBufferLimitBytes = Math.Max(1, bodyLimitMB) * 1024 * 1024;
        _dnsAccelEnabled = dnsAccelEnabled;
        _mitmEnabled = mitmEnabled && _ca is not null;
    }

    /// <summary>MITM 实际生效条件：开关打开、根 CA 就绪，且至少有一条白名单条目。</summary>
    public bool IsMitmActive =>
        _mitmEnabled && _ca is not null && _globalDnsWhitelist is { IsEmpty: false };

    /// <summary>把代理列表条目按 CDN 拆成多个优选池：跳过非法 IP、按出现顺序去重。</summary>
    private static Dictionary<string, IReadOnlyList<IPAddress>> BuildEdgePools(IEnumerable<ProxyEndpoint> edges)
    {
        var pools = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ep in edges)
        {
            if (ep is null || string.IsNullOrWhiteSpace(ep.Ip))
                continue;
            if (!IPAddress.TryParse(ep.Ip.Trim(), out var ip))
                continue;
            var cdn = string.IsNullOrWhiteSpace(ep.Cdn) ? CloudflareCdn : ep.Cdn.Trim();
            if (!pools.TryGetValue(cdn, out var list))
                pools[cdn] = list = new List<IPAddress>();
            if (!list.Contains(ip))
                list.Add(ip);
        }
        var result = new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in pools)
            result[kv.Key] = kv.Value;
        return result;
    }

    /// <summary>
    /// 定时清理 _hostCache / _bestEdgeCache 中已过期的条目；若仍超出容量上限，则按最早过期时间裁剪，
    /// 保证长跑（大量不同域名）时缓存不会无限增长。
    /// </summary>
    private void SweepCaches(object? state)
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _hostCache)
                if (kvp.Value.ExpiresUtc <= now)
                    _hostCache.TryRemove(kvp.Key, out _);
            foreach (var kvp in _bestEdgeCache)
                if (kvp.Value.ExpiresUtc <= now)
                    _bestEdgeCache.TryRemove(kvp.Key, out _);

            TrimByExpiry(_hostCache, MaxCacheEntries);
            TrimByExpiry(_bestEdgeCache, MaxCacheEntries);
        }
        catch
        {
            // 单次扫描失败不能让定时器停摆，忽略即可。
        }
    }

    private static void TrimByExpiry(ConcurrentDictionary<string, (string? Cdn, DateTime ExpiresUtc)> cache, int cap)
    {
        if (cache.Count <= cap)
            return;
        foreach (var key in cache.OrderBy(k => k.Value.ExpiresUtc).Take(cache.Count - cap).Select(k => k.Key).ToList())
            cache.TryRemove(key, out _);
    }

    private static void TrimByExpiry(ConcurrentDictionary<string, (IPAddress[] IPs, DateTime ExpiresUtc)> cache, int cap)
    {
        if (cache.Count <= cap)
            return;
        foreach (var key in cache.OrderBy(k => k.Value.ExpiresUtc).Take(cache.Count - cap).Select(k => k.Key).ToList())
            cache.TryRemove(key, out _);
    }

    public Task StartAsync(int port)
    {
        if (_listener is not null)
            throw new InvalidOperationException("代理已经启动");
        _cts = new CancellationTokenSource();
        _port = port;
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start(512);
        }
        catch
        {
            // 绑定失败别把 _listener/_cts/_port 留在半成品状态，否则「已经启动」的判断会骗住下一次重试
            listener.Stop();
            _cts.Dispose();
            _cts = null;
            _port = 0;
            throw;
        }
        _listener = listener;
        _acceptTask = AcceptLoopAsync(_cts.Token);
        _cacheSweepTimer = new Timer(SweepCaches, null, CacheSweepInterval, CacheSweepInterval);
        _log.Info($"代理已启动：127.0.0.1:{port}");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null)
            return;
        cts.Cancel();
        _listener?.Stop();
        try
        {
            if (_acceptTask is not null)
                await _acceptTask;
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }

        var active = _connections.Values.ToArray();
        await Task.WhenAny(Task.WhenAll(active), Task.Delay(2000));
        cts.Dispose();
        _cacheSweepTimer?.Dispose();
        _cacheSweepTimer = null;
        _cts = null;
        _listener = null;
        _port = 0;
        _acceptTask = null;
        _log.Info("代理已停止。");
    }

    /// <summary>accept 连续出错多少次后重建监听 socket（单次抖动先退避重试）。</summary>
    private const int AcceptErrorRebindThreshold = 3;

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        // 这个循环绝不能被一次异常带走：_acceptTask 只有 StopAsync 会 await（且只接取消类异常），
        // 循环一旦异常退出就是「静默永久死亡」——监听 socket 还开着，内核照常把新连接收进 backlog，
        // 浏览器 TCP 连得上却永远等不到响应（表现成 ERR_PROXY_CONNECTION_FAILED），系统代理又一直指着
        // 这个端口，UI 仍显示「运行中」，不重启程序不会自愈。睡眠唤醒时网卡/网络栈重置，挂起的
        // accept 几乎必然带着 SocketException 回来——正是原来那句 //todo 漏掉的场景。
        // 规则：只有取消/停止才退出；其它异常一律记日志 + 退避重试，确认 listener 废了就原地重建。
        var consecutiveErrors = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            // 记下这一轮 await 的是哪只 listener：唤醒重建会把旧的那只打断成异常（见下方 ReferenceEquals）
            var listener = _listener;
            if (listener is null)
                break;   // StopAsync 已收尾
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                consecutiveErrors = 0;
            }
            catch (Exception ex)
            {
                // StopAsync 是先 Cancel 再 Stop，正常停止都会从这里走（含 listener 已释放的 ObjectDisposed）
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (!ReferenceEquals(listener, _listener))
                {
                    // 我守着的那只已被 TryRebindListener（唤醒通知或上一轮重建）换掉：这次异常是预期内的，
                    // 下一轮直接用新的 listener 继续 Accept——不计错、不重复重建、不报警。
                    _log.Debug("accept 被监听 socket 重建打断，按新 listener 继续");
                    continue;
                }

                consecutiveErrors++;
                // ObjectDisposedException ＝ 这只 listener 已经废了，对着旧对象重试没有意义，立刻重建；
                // SocketException 之类先当瞬时抖动（唤醒初期网络栈还没就绪），连续到阈值再重建。
                var listenerDead = ex is ObjectDisposedException || consecutiveErrors >= AcceptErrorRebindThreshold;
                if (listenerDead)
                {
                    _log.Warn($"接受新连接连续 {consecutiveErrors} 次出错（{ex.GetType().Name}: {ex.Message}），重建监听 socket");
                    TryRebindListener(cancellationToken);
                }
                else
                {
                    _log.Debug($"接受新连接出错（连续第 {consecutiveErrors} 次，退避后重试）：{ex.GetType().Name} {ex.Message}");
                }

                var backoff = listenerDead ? TimeSpan.FromSeconds(Math.Min(consecutiveErrors, 5))
                                           : TimeSpan.FromMilliseconds(200);
                try { await Task.Delay(backoff, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            var id = Interlocked.Increment(ref _connectionId);
            var task = HandleClientSafeAsync(client, cancellationToken);
            _connections[id] = task;
            _ = task.ContinueWith(completed => _connections.TryRemove(id, out _), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// 系统唤醒通知（<see cref="MainWindow"/> 收 WM_POWERBROADCAST 后调用）：主动重建一次监听 socket。
    /// 睡眠把挂起的 accept 打死通常会以异常回来（走 <see cref="AcceptLoopAsync"/> 的重建路径），但协议栈
    /// 偶尔只是让它干等——那样就不会有任何异常，这条通知是补上的确定性自愈路径。
    /// 重建顺带把旧 listener 上的挂起 accept 打断，循环用「我 await 的那只已经不是当前这只」识别出来，
    /// 安静地换到新 listener 上继续，不会重复重建、也不会误报。
    /// </summary>
    public void NotifyNetworkResumed()
    {
        if (_port <= 0)
            return;   // 代理没在跑（或已停止）
        _log.Debug("收到系统唤醒通知，重建监听 socket");
        TryRebindListener(_cts?.Token ?? default);
    }

    /// <summary>
    /// 在原地重建监听 socket：唤醒后老 listener 可能已被网络栈拆掉；端口被占就记 ERROR 继续退避，
    /// 占用方消失后自动恢复。主调用点是 <see cref="AcceptLoopAsync"/> 与 <see cref="NotifyNetworkResumed"/>。
    /// </summary>
    private void TryRebindListener(CancellationToken cancellationToken)
    {
        var port = _port;
        if (port <= 0 || cancellationToken.IsCancellationRequested)
            return;   // 已停止/正在停止：别把端口占在一个没人 Accept 的新 listener 上

        try { _listener?.Stop(); } catch { }
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(512);
            _listener = listener;
            if (cancellationToken.IsCancellationRequested)
            {
                // 正好赶上 StopAsync：立刻让位，不能让端口活在一个已无人 Accept 的新 listener 上
                _listener = null;
                try { listener.Stop(); } catch { }
                return;
            }
            _log.Info($"监听 socket 已重建：127.0.0.1:{port}");
        }
        catch (Exception ex)
        {
            _log.Error($"重建监听 socket 失败（127.0.0.1:{port}）：{ex.GetType().Name} {ex.Message}");
        }
    }

    private async Task HandleClientSafeAsync(TcpClient client, CancellationToken serverToken)
    {
        using (client)
        {
            try
            {
                await HandleClientAsync(client, serverToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (serverToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // 单条连接的收尾异常不打日志的话，「连上却什么都不回」这类问题完全无从查起，记一条 DEBUG。
                _log.Debug($"代理连接收尾异常：{ex.GetType().Name} {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        client.NoDelay = true;
        await using var clientStream = client.GetStream();
        var http = new Http1Connection(clientStream);

        // 一条到代理的 TCP 连接是可以连续承载多个请求的（浏览器把这里当跨站点的复用连接池）。
        // 所以必须逐请求处理：读一个请求头 → 选路 → 转发 → 按帧中继响应 → 再读下一个。
        while (!serverToken.IsCancellationRequested)
        {
            HttpHead? head;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
            {
                // 空闲上限只作用在「等下一个请求头」上：浏览器挂着的 keep-alive 连接不能永久占着一个任务，
                // 但请求一旦开始转发就不设总时长上限（长下载/长响应跟 serverToken 走）。
                idle.CancelAfter(KeepAliveIdleTimeout);
                head = await http.ReadHeadAsync(idle.Token).ConfigureAwait(false);
            }
            if (head is null)
                return;   // 客户端干净地关掉了这条连接

            if (head.IsConnect)
            {
                await HandleConnectAsync(http, clientStream, head, serverToken).ConfigureAwait(false);
                return;   // 隧道 / MITM 解密自带双向泵，这条连接到此为止
            }

            if (!await ServePlainHttpRequestAsync(http, clientStream, head, serverToken).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>
    /// CONNECT：命中 MITM 白名单走解密，否则建立透明隧道。本方法返回即代表这条客户端连接结束。
    /// 隧道受「双向 60 秒无流量」的空闲上限约束（长下载/WebSocket 一直有字节就活着）；
    /// MITM 分支不受该时限（长下载正是它存在的理由），只跟随代理停止。
    /// </summary>
    private async Task HandleConnectAsync(
        Http1Connection http, Stream clientStream, HttpHead head, CancellationToken serverToken)
    {
        var authority = ProxyProtocol.ParseAuthority(head.RequestTarget, 443);
        // 浏览器可能把 CONNECT 和紧随其后的第一段 ClientHello 写进同一个包，剩余字节要先交给下游。
        var leftover = http.TakeBuffered();

        if (await ShouldMitmAsync(authority.Host, authority.Port, serverToken).ConfigureAwait(false))
        {
            await HandleMitmConnectAsync(clientStream, leftover, authority.Host, authority.Port, serverToken)
                .ConfigureAwait(false);
            return;
        }

        // 「建立后 60 秒硬掐」会把长下载/WebSocket 恰好在 1 分钟处掐断（浏览器＝代理报错）；
        // 改成空闲判定：任一方向有数据到达就把计时推后（PumpAsync 的 onActivity），双向 60 秒无流量才收连接。
        using var tunnelIdle = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        // 回调可能与收尾时的 Dispose 竞争，Cancel 包一层兜底
        using var idleTimer = new Timer(_ => { try { tunnelIdle.Cancel(); } catch { } }, null, TunnelIdleTimeout, Timeout.InfiniteTimeSpan);
        var cancellationToken = tunnelIdle.Token;
        void TouchIdle() => idleTimer.Change(TunnelIdleTimeout, Timeout.InfiniteTimeSpan);

        var upstream = await ConnectOrRespond502Async(clientStream, authority.Host, authority.Port,
            CloudflareHttpsPorts.Contains(authority.Port), cancellationToken).ConfigureAwait(false);
        if (upstream is null)
            return;   // 502 已经写给客户端了

        // 隧道是「一次 CONNECT 一条」，不会像请求那样刷屏；这个计数让状态栏能说清「没解密的 CONNECT 有多少」。
        _stats.AddTunnel();

        using (upstream)
        {
            await using var upstreamStream = upstream.GetStream();
            await clientStream.WriteAsync("HTTP/1.1 200 Connection Established\r\nProxy-Agent: CloudflareSmartProxy/1.0\r\n\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            if (leftover.Length > 0)
                await upstreamStream.WriteAsync(leftover, cancellationToken).ConfigureAwait(false);
            await RelayBothWaysAsync(clientStream, upstreamStream, cancellationToken, TouchIdle).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 明文 HTTP 的「一个」请求：absolute-form 改写成 origin-form → 走原有选路（CDN 优选 / DNS 加速 / 系统 DNS）连上游
    /// → 按帧转发请求体与中继响应头/响应体，然后回到 <see cref="HandleClientAsync"/> 的循环接下一个请求。
    /// 上游一律「一请求一连接」（发 <c>Connection: close</c>）：每条请求都要重新选路，
    /// 复用上一条请求的上游连接会把后面的请求原样塞进上一个站点的连接里——那正是「刷新一次 404、再刷新又正常」的根源。
    /// </summary>
    /// <returns>false 表示这条客户端连接应当结束（响应长度只能由上游关闭界定、客户端要求关闭、或中途出错）。</returns>
    private async Task<bool> ServePlainHttpRequestAsync(
        Http1Connection http, Stream clientStream, HttpHead head, CancellationToken serverToken)
    {
        var keepAlive = ProxyProtocol.WantsKeepAlive(head);

        if (!ProxyProtocol.TryParseForwardTarget(head.RequestTarget, out var target))
        {
            await WriteErrorAsync(clientStream, 400, "仅支持 HTTP absolute-form 请求或 HTTPS CONNECT", serverToken)
                .ConfigureAwait(false);
            return false;
        }

        if (ProxyProtocol.HasAmbiguousBodyFraming(head))
        {
            _log.Debug($"明文 HTTP 拒绝 {target.Host}:{target.Port} → Content-Length 与 chunked 冲突（请求走私特征）");
            await WriteErrorAsync(clientStream, 400, "请求体长度声明冲突", serverToken).ConfigureAwait(false);
            return false;
        }

        using var upstream = await ConnectOrRespond502Async(clientStream, target.Host, target.Port,
            CloudflareHttpPorts.Contains(target.Port), serverToken).ConfigureAwait(false);
        if (upstream is null)
            return false;   // 502 已经写给客户端了

        await using var upstreamStream = upstream.GetStream();
        var upstreamHttp = new Http1Connection(upstreamStream);

        try
        {
            // 改写后的请求行/头发给上游（不是客户端！），随后按帧把请求体也从客户端搬到上游
            await upstreamStream.WriteAsync(ProxyProtocol.BuildForwardRequestHead(head, target), serverToken).ConfigureAwait(false);
            await upstreamStream.FlushAsync(serverToken).ConfigureAwait(false);

            // Expect: 100-continue：由代理自己先放行，客户端才会把请求体发上来（Expect 头不转给源站）
            if (head.HasHeader("Expect"))
                await http.WriteRawStringAsync("HTTP/1.1 100 Continue\r\n\r\n", serverToken).ConfigureAwait(false);

            // 请求体按帧转发：chunked 原帧中继（请求头里的 Transfer-Encoding 一并保留），定长按字节数中继
            if (head.IsChunked)
                await http.RelayChunkedAsync(upstreamStream, serverToken).ConfigureAwait(false);
            else if (head.ContentLength is long requestLength && requestLength > 0)
                await http.RelayFixedAsync(upstreamStream, requestLength, serverToken).ConfigureAwait(false);

            var response = await upstreamHttp.ReadHeadAsync(serverToken).ConfigureAwait(false);
            // 1xx 临时响应（101 除外）原样透传给客户端，继续读最终响应
            while (response is not null && response.StatusCode >= 100 && response.StatusCode < 200 && response.StatusCode != 101)
            {
                await http.WriteAsync(response.RawBytes, serverToken).ConfigureAwait(false);
                response = await upstreamHttp.ReadHeadAsync(serverToken).ConfigureAwait(false);
            }

            if (response is null)
            {
                _log.Debug($"明文 HTTP {target.Host}:{target.Port} → 上游在返回响应头前关闭连接");
                await WriteErrorAsync(clientStream, 502, "上游在返回响应头前关闭连接", serverToken).ConfigureAwait(false);
                return false;
            }

            var noBody = string.Equals(head.Method, "HEAD", StringComparison.OrdinalIgnoreCase)
                         || response.StatusCode is 204 or 304;
            // 响应长度只能由「上游关闭」来界定 → 这条客户端连接也得跟着关，否则下一个请求会读到上一个站点的残余字节
            var untilClose = !noBody && !response.IsChunked && response.ContentLength is null;
            var closeClient = untilClose || response.StatusCode == 101 || !keepAlive;

            await http.WriteAsync(MitmSession.RewriteResponseHead(response, close: closeClient), serverToken)
                .ConfigureAwait(false);
            await http.FlushAsync(serverToken).ConfigureAwait(false);

            if (response.StatusCode == 101)
            {
                // 协议升级（h2c 之类）：之后的字节不再是 HTTP 帧，只能双向裸泵到任一方关闭；先把已缓冲的后续字节补给上游
                var pending = http.TakeBuffered();
                if (pending.Length > 0)
                    await upstreamStream.WriteAsync(pending, serverToken).ConfigureAwait(false);
                await RelayBothWaysAsync(clientStream, upstreamStream, serverToken).ConfigureAwait(false);
                return false;
            }

            if (noBody)
                return !closeClient;

            // 帧判定优先看 chunked：Transfer-Encoding 存在时以它为准（上面的 400 校验只覆盖请求侧，上游不一致同样要防）
            if (response.IsChunked)
                await upstreamHttp.RelayChunkedAsync(clientStream, serverToken).ConfigureAwait(false);
            else if (response.ContentLength is long responseLength)
                await upstreamHttp.RelayFixedAsync(clientStream, responseLength, serverToken).ConfigureAwait(false);
            else
            {
                await upstreamHttp.RelayPumpAsync(clientStream, serverToken).ConfigureAwait(false);
                return false;   // untilClose
            }

            return !closeClient;
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                   or MalformedHttpException or ObjectDisposedException)
        {
            // 任一端半路断开（浏览器取消请求、源站提前关闭、帧不合法）都是正常流量，收尾这条连接即可
            _log.Debug($"明文 HTTP {head.Method} {target.Host}:{target.Port} → 转发中断：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 建立上游连接；「加速池 + 系统 DNS 的全部候选都试过仍连不上」时给客户端回 502 并返回 null，
    /// 其它失败（含取消）照旧向上抛，由 <see cref="HandleClientSafeAsync"/> 收尾。
    /// </summary>
    private async Task<TcpClient?> ConnectOrRespond502Async(
        Stream clientStream, string host, int port, bool optimizablePort, CancellationToken cancellationToken)
    {
        try
        {
            return await ConnectForHostAsync(host, port, optimizablePort, cancellationToken).ConfigureAwait(false);
        }
        catch (UpstreamUnavailableException ex)
        {
            _log.Warn(ex.Message);
            await WriteErrorAsync(clientStream, 502, $"CloudflareSmartProxy: {ex.Message}", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
    }

    private async Task<TcpClient> ConnectForHostAsync(string host,int port, bool optimizablePort,CancellationToken cancellationToken)
    {
        // 先判断 host 归属哪家已接入的 CDN（Cloudflare 优先，保持原分流行为）；
        // 只有端口在可优化白名单内才值得做这次判断。
        var cdn = optimizablePort
            ? await ClassifyCdnCachedAsync(host, cancellationToken).ConfigureAwait(false)
            : null;

        if (cdn is not null &&
            _edgePools.TryGetValue(cdn, out var edges) &&
            edges.Count > 0 &&
            // 非 Cloudflare 档位只加速标准端口：CF 专属的非标 HTTPS/HTTP 端口约定不适用于其它 CDN
            (string.Equals(cdn, CloudflareCdn, StringComparison.OrdinalIgnoreCase) || port is 443 or 80))
        {
            //情况1： 该域名属于已接入的 CDN，且有可用的优选 IP 池，尝试优选 IP 连接。
            var cacheKey = cdn + "|" + host;
            // ---------------------------------------------------------
            // 先看看这个域名有没有已经验证过的最佳 IP（缓存键含 CDN，避免跨池命中）
            // ---------------------------------------------------------

            if (_bestEdgeCache.TryGetValue(cacheKey, out var cached) &&
                cached.ExpiresUtc > DateTime.UtcNow)
            {
                //cached.IPs的顺序打乱，避免每次都尝试同一个 IP
                var rng = new Random();
                var shuffledIps = cached.IPs.OrderBy(x => rng.Next()).ToArray();
                foreach (var ip in shuffledIps)
                {
                    try
                    {
                        var client = await ConnectIpAsync(
                            ip,
                            port,
                            cancellationToken
                        ).ConfigureAwait(false);

                        // 高频成功日志只进文件（DEBUG 不进 UI），实时可见性交给窗口底部状态栏的计数。
                        _stats.AddEdgeConnection();
                        _stats.RecordWinner(host, ip, 0);
                        _log.Debug(
                            $"{cdn} → {host}:{port} → 缓存优选 {ip}"
                        );

                        return client;
                    }
                    catch
                    {
                        // 尝试下一个候选 IP
                    }
                }
                // 所有缓存候选都不可用，移除缓存并继续接下来的流程
                _bestEdgeCache.TryRemove(cacheKey, out _);
                _log.Debug(
                    $"{cdn} → {host}:{port} → 缓存优选 IP 全部不可用，移除缓存"
                );
            }

            // ---------------------------------------------------------
            // 没有缓存：
            // 并发 HEAD 验证该 CDN 池里的所有优选 IP
            // 因为浏览器会同时打开多个TCP连接，所以在ConnectForHostAsync存在竞争条件，
            // 会调用FindBestEdgeAsync多次（在没有获取到缓存之前），所以_bestEdgeCache需要保存多个IP，这样可以实现每个链接可能的远端IP不同，来实现加速。
            // ---------------------------------------------------------

            var bestIp = await FindBestEdgeAsync(
                cdn,
                host,
                edges,
                cancellationToken
            ).ConfigureAwait(false);

            if (bestIp != null)
            {
                try
                {
                    var connected = await ConnectIpAsync(
                        bestIp,
                        port,
                        cancellationToken
                    ).ConfigureAwait(false);

                    var expiry = DateTime.UtcNow.AddMinutes(20);//todo 20分钟是否合理？
                    //添加缓存或更新缓存。
                    _bestEdgeCache.AddOrUpdate(
                        cacheKey,
                        (
                            // 没有的情况下就添加。
                            new[] { bestIp }.ToArray(),
                            expiry
                        ),
                        (key, existing) =>
                        {
                            // 有的情况下就合并
                            var set = new HashSet<IPAddress>(existing.IPs);
                            set.Add(bestIp);
                            return (set.ToArray(), expiry);
                        }
                    );

                    // 同上：每次连接一条的成功日志降到 DEBUG，用状态栏计数体现「优选在生效」。
                    _stats.AddEdgeConnection();
                    _stats.RecordWinner(host, bestIp, 0);
                    _log.Debug(
                        $"{cdn} → {host}:{port} → 优选 {bestIp}"
                    );

                    return connected;
                }
                catch (Exception ex)
                    when (ex is SocketException ||
                          ex is OperationCanceledException)
                {
                    _log.Warn(
                        $"{cdn} → {host}:{port} → 优选 {bestIp} 连接失败"
                    );
                }
            }

            _log.Warn(
                $"{cdn} → {host}:{port} → 没有找到可用优选 IP，回退原始 DNS"
            );
        }
        else if (port is 443 or 80)
        {
            // 情况2 闸门：host 必须命中「设置」页的 DNS 加速白名单，未命中（或白名单为空）直接走原始 DNS
            var whitelist = _globalDnsWhitelist;
            if (_dnsAccelEnabled && whitelist is { IsEmpty: false } && whitelist.IsMatch(host))
            {
                var (raced, exhausted) = await ConnectViaDnsPoolAsync(host, port, cancellationToken).ConfigureAwait(false);
                if (raced is not null)
                    return raced;

                // 加速池 + 系统 DNS 已经全扫完并逐 IP 试过：原始 DNS 解析出的就是同一批地址，不再重复回退
                if (exhausted)
                    throw new UpstreamUnavailableException($"{host}:{port} 的全部候选 IP 均连不上（DNS 加速池与系统 DNS 已用尽）");

                _log.Warn(
                    $"{host}:{port} → DNS 加速池解析/竞速未成功，回退原始 DNS"
                );
            }
            //else
            //{
            //    _log.Warn(
            //        $"{host}:{port} → 回退原始 DNS"
            //    );
            //}
        }
        //退回默认
        return await ConnectHostAsync(
            host,
            port,
            cancellationToken
        ).ConfigureAwait(false);
    }

    /// <summary>
    /// 情况2（未解密路径）：用 DNS 加速池解析 host，再对解析到的多个 IP 做并发 TCP 竞速，返回最快连上的连接。
    /// 不解密内容，HTTPS 仍是端到端 TLS，只是「接入点」换成了最快的那个 IP。
    /// 同一域名只有一次全池扫描（浏览器那几条连接共享同一个条目）：每拿出一批本连接还没试过的 IP
    /// 就并发连一次，都不通就等扫描补新 IP；扫描已扫完且全都试过之后，按 <see cref="ProxyOptions.RaceRetryRounds"/>
    /// 的额度隔 <see cref="ProxyOptions.RaceRetryDelayMs"/> 把同一批已知地址再重赛几轮，最后才判失败。
    /// 返回 (连接, 解析手段是否已用尽)：连接为 null 且已用尽时调用方直接报错，不必再回退一次原始 DNS。
    /// </summary>
    private async Task<(TcpClient? Client, bool Exhausted)> ConnectViaDnsPoolAsync(
        string host, int port, CancellationToken cancellationToken)
    {
        var tried = new HashSet<IPAddress>();
        var retriesUsed = 0;
        TcpClient? client = null;

        // 取「本连接还没试过」的 IP；全池扫完且全都试过之后，按「设置」页的额度等一会儿，
        // 把现在已知的同一批地址再重赛一轮（候选 IP 不被拉黑，治瞬时的「刚才不行、过一会又行」）。
        async Task<IReadOnlyList<IPAddress>?> NextBatchAsync()
        {
            var fresh = await _resolver.WaitForNewIpsAsync(host, tried, cancellationToken).ConfigureAwait(false);
            if (fresh is not null) return fresh;
            if (tried.Count == 0)
                return null;   // 一个 IP 都没解析出来：重赛没有意义，NegativeTtl 会安排下一次扫描

            var again = await _resolver.RetryKnownIpsAsync(
                host, retriesUsed, _options.RaceRetryRounds, _options.RaceRetryDelayMs, cancellationToken).ConfigureAwait(false);
            if (again is null) return null;

            retriesUsed++;
            _log.Warn($"{host}:{port} → 已知 {tried.Count} 个候选 IP 全部连不上，第 {retriesUsed}/{_options.RaceRetryRounds} 轮重赛");
            return again;
        }

        try
        {
            while (await NextBatchAsync() is { } batch)
            {
                foreach (var ip in batch) tried.Add(ip);

                try
                {
                    client = await _racer.RaceTcpConnectAsync(batch, port, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn($"{host} → TCP 竞速异常：{ex.Message}");
                    return (null, false);
                }
                if (client is not null) break;
                if (cancellationToken.IsCancellationRequested) return (null, false);

                if (!_resolver.IsScanCompleted(host))
                    _log.Warn($"{host}:{port} → 本批 {batch.Count} 个 IP 均连不上（累计已试 {tried.Count} 个），等 DNS 扫描补新 IP");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"{host} → DNS 加速池解析异常：{ex.Message}");
            return (null, false);
        }

        if (client is null)
        {
            // 扫描的最后一手就是系统 DNS，所以「已扫完 + 一个都没连上」＝原始 DNS 那条路也试过了，不必再回退一次
            var exhausted = tried.Count > 0 && _resolver.IsScanCompleted(host) && !cancellationToken.IsCancellationRequested;
            if (tried.Count > 0)
                _log.Warn($"{host} → {tried.Count} 个候选 IP 均连不上（加速池与系统 DNS 已扫完：{exhausted}）");
            return (null, exhausted);
        }

        var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (remoteIp is not null)
            _stats.RecordWinner(host, remoteIp, 0);
        _log.Debug(
            $"DNS 加速 → {host}:{port} → 竞速胜出 {(remoteIp?.ToString() ?? "未知")}（累计候选 {tried.Count} 个 IP，池内 DNS {(_resolver.HasScanResults ? _resolver.RankedServers.Count + " 个" : "未启用")}）"
        );
        return (client, false);
    }

    /// <summary>
    /// 该 CONNECT 是否应当做 MITM 解密：需同时满足 DNS 加速开启、MITM 开启、根 CA 就绪、
    /// 端口在 MitmPorts 内、host 命中白名单，且不属于「情况1」（已接入 CDN 且有可用优选池的域名继续走原隧道逻辑）。
    /// </summary>
    private async Task<bool> ShouldMitmAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (!_dnsAccelEnabled || !_mitmEnabled || _ca is null)
            return false;
        if (!_options.MitmPorts.Contains(port))
            return false;
        var whitelist = _globalDnsWhitelist;
        if (whitelist is null || whitelist.IsEmpty || !whitelist.IsMatch(host))
            return false;

        // 情况1 优先：能被 CDN 优选池加速的域名不解密（ClassifyCdnCachedAsync 有缓存，重复判定开销可忽略）
        var optimizablePort = CloudflareHttpsPorts.Contains(port) || CloudflareHttpPorts.Contains(port);
        var cdn = optimizablePort
            ? await ClassifyCdnCachedAsync(host, cancellationToken).ConfigureAwait(false)
            : null;
        if (cdn is not null &&
            _edgePools.TryGetValue(cdn, out var edges) &&
            edges.Count > 0 &&
            (string.Equals(cdn, CloudflareCdn, StringComparison.OrdinalIgnoreCase) || port is 443 or 80))
            return false;

        return true;
    }

    /// <summary>
    /// CONNECT 的 MITM 处理：回 200 后用根 CA 签发的域名证书与客户端完成 TLS 握手，
    /// 之后交给 <see cref="MitmSession"/> 逐请求竞速转发。
    /// 与其它路径的差别：这里不受「每连接 60 秒」硬时限约束（长下载/长轮询会被它掐断），只跟随代理停止。
    /// </summary>
    private async Task HandleMitmConnectAsync(
        Stream clientStream, byte[] leftover, string host, int port, CancellationToken serverToken)
    {
        var ca = _ca ?? throw new InvalidOperationException("根证书未初始化");
        _stats.AddMitm();

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var token = sessionCts.Token;

        await clientStream.WriteAsync(
            "HTTP/1.1 200 Connection Established\r\nProxy-Agent: CloudflareSmartProxy/1.0\r\n\r\n"u8.ToArray(),
            token).ConfigureAwait(false);

        // 客户端可能把 CONNECT 和 ClientHello 写在同一个包里，剩余字节要先喂给 TLS 流。
        var tlsSource = leftover.Length > 0 ? new PrefixedStream(clientStream, leftover) : clientStream;
        using var ssl = new SslStream(tlsSource, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = ca.GetOrCreateDomainCertificate(host),
                EnabledSslProtocols = SslProtocols.None,
                ClientCertificateRequired = false,
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException
                                  or OperationCanceledException or ObjectDisposedException)
        {
            // 多半是浏览器不信任我们的根证书（未安装/未确认）后主动断开。
            _log.Warn($"MITM 握手失败 {host}：{ex.GetType().Name} {ex.Message}（若未安装根证书，请到「设置」页安装）");
            return;
        }

        await new MitmSession(ssl, host, port, _resolver, _racer, _options, _stats)
            .RunAsync(token).ConfigureAwait(false);
    }

    
    /// <summary>
    /// 并发 HEAD 验证所选 CDN 池里的所有优选 IP，返回第一个成功的 IP，不等待所有 IP 测试完成
    /// </summary>
    /// <param name="host"></param>
    /// <param name="edges"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<IPAddress?> FindBestEdgeAsync(string cdn,string host, IReadOnlyList<IPAddress> edges, CancellationToken cancellationToken)
    {
        using var cts =
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var tasks = edges.Select(edge =>
            Task.Run(
                () => TestEdgeHeadAsync(host, edge, cts.Token),
                cts.Token
            )
        ).ToList();
        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks).ConfigureAwait(false);

            tasks.Remove(completed);

            HeadTestResult result;

            try
            {
                result = await completed.ConfigureAwait(false);
            }
            catch
            {
                continue;
            }
            if (result.Success)
            {
                cts.Cancel();

                _log.Debug(
                    $"{cdn} → {host} → 首个可用IP: " +
                    $"{result.IP}, " +
                    $"HTTP {result.StatusCode}, " +
                    $"{result.TotalMs}ms"
                );

                return result.IP;
            }
        }
        return null;
    }
    /// <summary>
    /// 对指定 host + IP + path 做 HEAD 请求，快速返回结果
    /// </summary>
    /// <param name="host">目标域名，例如 www.relaxsleepmusic.com</param>
    /// <param name="ip">Cloudflare Edge IP</param>
    /// <param name="path">当前请求路径，例如 / 或 /assets/a.js</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<HeadTestResult> TestEdgeHeadAsync(string host,IPAddress ip,CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            // 单个 IP 最多 5 秒
            timeout.CancelAfter(5000);

            using var socket = new Socket(
                ip.AddressFamily,
                SocketType.Stream,
                ProtocolType.Tcp);

            socket.NoDelay = true;

            // ---------------------------------------------------------
            // TCP
            // ---------------------------------------------------------

            await socket.ConnectAsync(
                new IPEndPoint(ip, 443),
                timeout.Token
            ).ConfigureAwait(false);

            using var networkStream =
                new NetworkStream(
                    socket,
                    ownsSocket: true);

            // ---------------------------------------------------------
            // TLS
            //
            // TCP 实际连接：
            //     Cloudflare IP:443
            //
            // TLS SNI：
            //     www.relaxsleepmusic.com
            // ---------------------------------------------------------

            using var ssl = new SslStream(
                networkStream,
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback:
                    (_, _, _, _) => true);

            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = host,

                    EnabledSslProtocols =
                        SslProtocols.Tls12 |
                        SslProtocols.Tls13,

                    CertificateRevocationCheckMode =
                        System.Security.Cryptography.X509Certificates
                            .X509RevocationMode.NoCheck
                },
                timeout.Token
            ).ConfigureAwait(false);

            // ---------------------------------------------------------
            // HEAD
            // ---------------------------------------------------------

            var request =
                $"GET / HTTP/1.1\r\n" +
                $"Host: {host}\r\n" +
                $"Connection: close\r\n" +
                $"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                $"AppleWebKit/537.36 (KHTML, like Gecko) " +
                $"Chrome/152.0.0.0 Safari/537.36\r\n" +
                $"\r\n";

            var requestBytes =
                Encoding.ASCII.GetBytes(request);

            await ssl.WriteAsync(
                requestBytes,
                timeout.Token
            ).ConfigureAwait(false);

            // ---------------------------------------------------------
            // 只读取 HTTP Header
            // ---------------------------------------------------------

            var buffer = new byte[4096];

            int count = await ssl.ReadAsync(
                buffer,
                timeout.Token
            ).ConfigureAwait(false);

            if (count <= 0)
            {
                return new HeadTestResult
                {
                    IP = ip,
                    Success = false,
                    Error = "EMPTY_RESPONSE",
                    TotalMs = sw.ElapsedMilliseconds
                };
            }

            var response =
                Encoding.ASCII.GetString(
                    buffer,
                    0,
                    count);

            var statusCode =
                Function.ParseStatusCode(response);

            _log.Debug($"Head test {host} -> {ip} statusCode:{statusCode}");

            // ---------------------------------------------------------
            // 200 / 204 / 301 / 302 / 304 / 404 都认为这个 IP 可以使用
            // ---------------------------------------------------------

            /*bool success =
                statusCode == 200 ||
                statusCode == 204 ||
                statusCode == 301 ||
                statusCode == 302 ||
                statusCode == 304 ||
                statusCode == 403 ||
                statusCode == 404;*/
            // 如果是 403，解析并获取 Cf-Mitigated 的值，需要根据Cf-Mitigated=challenge来判断是否是跳了cloudflare验证，而不是真正的边缘节点没有该域名的访问权限。
            bool success = true;
            if (statusCode == 403)
            {
                if(response.Contains("error code: 1034"))
                {
                    _log.Debug($"Head test {host} -> {ip} Edge IP Restricted. ");
                    success = false;
                }
                else
                {
                    success = true;
                }
            }

            return new HeadTestResult
            {
                IP = ip,
                Success = success,
                StatusCode = statusCode,
                TotalMs = sw.ElapsedMilliseconds,
                Error = success
                    ? null
                    : $"HTTP_{statusCode}"
            };
        }
        catch (OperationCanceledException)
        {
            return new HeadTestResult
            {
                IP = ip,
                Success = false,
                Error = "TIMEOUT",
                TotalMs = sw.ElapsedMilliseconds
            };
        }
        catch (SocketException ex)
        {
            return new HeadTestResult
            {
                IP = ip,
                Success = false,
                Error = $"SOCKET_{ex.SocketErrorCode}",
                TotalMs = sw.ElapsedMilliseconds
            };
        }
        catch (AuthenticationException ex)
        {
            return new HeadTestResult
            {
                IP = ip,
                Success = false,
                Error = "TLS_" + ex.Message,
                TotalMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new HeadTestResult
            {
                IP = ip,
                Success = false,
                Error = ex.GetType().Name,
                TotalMs = sw.ElapsedMilliseconds
            };
        }
        finally
        {
            sw.Stop();
        }
    }

    /// <summary>
    /// 判断 host 属于哪家已接入的 CDN：解析一次 DNS，按 _providers 顺序（Cloudflare 最先）依次匹配网段。
    /// 结果缓存：命中 60 分钟、未命中 15 分钟（与原 IsCloudflareCachedAsync 的 TTL 完全一致）。
    /// </summary>
    private async Task<string?> ClassifyCdnCachedAsync(string host, CancellationToken cancellationToken)
    {
        if (_hostCache.TryGetValue(host, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return cached.Cdn;

        string? cdn = null;
        try
        {
            var addresses = await ResolveForClassifyAsync(host, cancellationToken).ConfigureAwait(false);
            foreach (var provider in _providers)
            {
                if (addresses.Any(provider.Contains))
                {
                    cdn = provider.Name;
                    break;
                }
            }
        }
        catch
        {
            cdn = null;
        }

        _hostCache[host] = (cdn, DateTime.UtcNow.AddMinutes(cdn is null ? 15 : 60));
        return cdn;
    }

    /// <summary>
    /// 分类用的系统 DNS 解析，带 <see cref="ClassifyDnsTimeout"/> 时限：系统解析没有自己的超时，
    /// VPN 切换 / DNS Client 服务异常时每个新域名都会被吊在这里，表现为「代理整体无响应」。
    /// 超时按解析失败处理（空数组 → 不属于任何 CDN → 该域名走原始 DNS 直连），别把连接吊死在分类上。
    /// </summary>
    private async Task<IPAddress[]> ResolveForClassifyAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal)) return new[] { literal };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ClassifyDnsTimeout);
            return await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Debug($"CDN 归属判定 {host} → 系统 DNS 解析超时（{ClassifyDnsTimeout.TotalSeconds:0}s），按非 CDN 处理");
            return Array.Empty<IPAddress>();
        }
    }

    private static async Task<TcpClient> ConnectIpAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var client = new TcpClient(address.AddressFamily) { NoDelay = true };
        try
        {
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<TcpClient> ConnectHostAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task RelayBothWaysAsync(Stream a, Stream b, CancellationToken cancellationToken, Action? onActivity = null)
    {
        using var localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var aToB = PumpAsync(a, b, localCts.Token, onActivity);
        var bToA = PumpAsync(b, a, localCts.Token, onActivity);
        var completed = await Task.WhenAny(aToB, bToA).ConfigureAwait(false);
        // cancel the other direction so it can stop promptly
        try { localCts.Cancel(); } catch { }
        await Task.WhenAll(aToB, bToA).ConfigureAwait(false);
    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken, Action? onActivity = null)
    {
        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                // 有真实数据到达才算「活跃」：隧道的空闲计时靠它推后（总时长不再设限）
                onActivity?.Invoke();
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
        finally
        {
            pool.Return(buffer);
        }
    }

    private static async Task WriteErrorAsync(Stream stream, int status, string message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message);
        // 原因短语要跟状态码一致（CONNECT 失败回 502 时浏览器会按状态码处理）
        var reason = status switch
        {
            400 => "Bad Request",
            502 => "Bad Gateway",
            504 => "Gateway Timeout",
            _ => "Error",
        };
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }
}
