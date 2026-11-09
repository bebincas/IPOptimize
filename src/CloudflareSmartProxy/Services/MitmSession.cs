using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 白名单域名的 MITM 会话：代理已与浏览器完成「服务端」TLS 握手（用根 CA 签发的域名证书），
/// 这里把内层当作普通 HTTP 处理。上游按两种模式选路：
/// 幂等且无请求体的方法（GET/HEAD/OPTIONS）才做「逐请求广播竞速」——同一请求并发发给多个候选 IP，
/// 最快返回完整响应头的胜出（透明隧道做不到这一点）；其余（POST/PUT/PATCH/DELETE 或任何带体请求）
/// 走**单发模式**：并发只做 TCP+TLS 握手竞速，赢的那条连接才发请求，保证远端至多执行一次。
/// 只处理「设置」页 DNS 加速白名单命中的域名，其余 HTTPS 仍走端到端透明隧道。
/// </summary>
public sealed class MitmSession
{
    private readonly Http1Connection _client;
    private readonly ResolverHub _resolver;
    private readonly UpstreamRacer _racer;
    private readonly ProxyOptions _options;
    private readonly ProxyStatistics _stats;
    private readonly string _host;
    private readonly int _port;

    public MitmSession(
        Stream clientTlsStream,
        string host,
        int port,
        ResolverHub resolver,
        UpstreamRacer racer,
        ProxyOptions options,
        ProxyStatistics stats)
    {
        _client = new Http1Connection(clientTlsStream);
        _host = host;
        _port = port;
        _resolver = resolver;
        _racer = racer;
        _options = options;
        _stats = stats;
    }

    /// <summary>keep-alive 主循环：逐个请求竞速转发，直到客户端关闭或连接不再保持。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var head = await _client.ReadHeadAsync(ct);
            if (head is null) return;
            if (!await ServeRacedAsync(head, ct)) return;
        }
    }

    /// <summary>返回 false 表示这条客户端连接应当结束（keep-alive 关闭或长度未知的响应）。</summary>
    private async Task<bool> ServeRacedAsync(HttpHead reqHead, CancellationToken ct)
    {
        _stats.AddRequest();

        // Expect: 100-continue → 先放行请求体
        var hasExpect = reqHead.HasHeader("Expect");

        // 请求体：小 → 缓冲（广播竞速要把同一份请求发给多个 IP；单发模式也复用这份缓冲，只发给赢家）；大 → 退化为单连接流式
        byte[]? body = null;
        bool bodyTooLarge = false;
        if (reqHead.ContentLength is long cl && cl > 0)
        {
            if (hasExpect) await _client.WriteRawStringAsync("HTTP/1.1 100 Continue\r\n\r\n", ct);
            if (cl > _options.RequestBodyBufferLimitBytes) bodyTooLarge = true;
            else body = await _client.ReadExactAsync(cl, ct);
        }
        else if (reqHead.IsChunked)
        {
            if (hasExpect) await _client.WriteRawStringAsync("HTTP/1.1 100 Continue\r\n\r\n", ct);
            try { body = await _client.ReadChunkedBodyAsync(_options.RequestBodyBufferLimitBytes, ct); }
            catch (RequestBodyTooLargeException) { bodyTooLarge = true; }
        }

        if (bodyTooLarge)
        {
            AppLog.Info($"请求体超过 {_options.RequestBodyBufferLimitBytes / 1024 / 1024}MB，退化为单连接直连: {_host}");
            return await ServeSingleAsync(reqHead, ct);
        }

        var headBytes = BuildUpstreamRequestHead(
            reqHead,
            deChunked: body is not null && reqHead.IsChunked,
            contentLength: body is null ? reqHead.ContentLength : body.LongLength);

        // 广播竞速（每个候选 IP 各发一份完整请求，谁先回响应头谁赢）只允许用在幂等且无请求体的方法上。
        // POST/PUT/PATCH/DELETE 或任何带体的请求并发发 N 份＝远端重复执行 N 次（重复下单/扣款/发验证码），
        // 取消输家救不回来——字节早就出去了。这类请求走单发：先握手竞速，赢家只发一份。
        if (!IsFanoutSafe(reqHead.Method, body))
            return await ServeSingleSendAsync(reqHead, headBytes, body, ct).ConfigureAwait(false);

        // 共享解析条目里现在有哪些 IP 就并发试哪些；全连不上就等这个域名的全池扫描补上新 IP，
        // 直到「加速池 + 系统 DNS 已扫完 且 列表里的地址本连接都试过了」才判失败。
        var sw = Stopwatch.StartNew();
        var (winner, triedCount) = await RaceKnownIpsAsync(headBytes, body, ct);
        if (winner is null)
        {
            _stats.AddError();
            // 连不上是传输层的事，这里一个字节都不回，直接结束会话：RunAsync 返回后 HandleMitmConnectAsync 里的
            // using ssl 会发 TLS close_notify 并关掉底层 socket，Chrome 得到 ERR_EMPTY_RESPONSE，与真断网一致
            // （fetch 会 reject，页面自己的离线处理才生效）。发任何状态码都会被当成「网站正常响应了 5xx」：
            // 我们的文字会变成页面正文、SW 可能缓存、前端还会去 JSON.parse 它。原因上面 RaceKnownIpsAsync 已写进日志。
            return false;
        }
        using (winner)
        {
            _stats.RecordWinner(_host, winner.Ip, (int)sw.ElapsedMilliseconds);
            // 每个请求一条 → 只写文件；UI 看状态栏的「解密请求 / 最近胜出」。
            AppLog.Debug($"[MITM] {reqHead.Method} {_host} → 竞速胜出 {winner.Ip}" +
                        $"（{winner.ElapsedMs}ms 建连，{sw.ElapsedMilliseconds}ms 响应头，累计候选 {triedCount} 个 IP）");
            return await RelayUpstreamResponseAsync(winner, reqHead.Method, ct);
        }
    }

    /// <summary>
    /// 「解析 → 竞速」直到成功或确认没有更多 IP：从共享条目里取本连接还没试过的 IP 全部并发试
    /// （同批内首个返回响应头的胜出），都不行就等全池扫描追加新 IP；扫描已扫完且全都试过之后，
    /// 按「设置」页的额度隔一会儿把现在已知的同一批地址再重赛几轮。
    /// 试过的 IP 只在这条连接内记账、不从缓存删除（连不上多半是瞬时的，别的连接和稍后仍该能用），
    /// 重赛也只是「再试一遍」，不是惩罚表意义上的复活。
    /// </summary>
    private async Task<(UpstreamResponse? Winner, int TriedCount)> RaceKnownIpsAsync(
        byte[] headBytes, byte[]? body, CancellationToken ct)
    {
        var tried = new HashSet<IPAddress>();
        var retriesUsed = 0;

        // 取「本连接还没试过」的 IP；没有新 IP 了再看重赛额度还有没有
        async Task<IReadOnlyList<IPAddress>?> NextBatchAsync()
        {
            var fresh = await _resolver.WaitForNewIpsAsync(_host, tried, ct).ConfigureAwait(false);
            if (fresh is not null) return fresh;
            if (tried.Count == 0)
                return null;   // 一个 IP 都没解析出来：重赛没有意义，NegativeTtl 会安排下一次扫描

            var again = await _resolver.RetryKnownIpsAsync(
                _host, retriesUsed, _options.RaceRetryRounds, _options.RaceRetryDelayMs, ct).ConfigureAwait(false);
            if (again is null) return null;

            retriesUsed++;
            AppLog.Warn($"[MITM] {_host} 已知 {tried.Count} 个候选 IP 均无响应，第 {retriesUsed}/{_options.RaceRetryRounds} 轮重赛");
            return again;
        }

        while (await NextBatchAsync() is { } batch)
        {
            foreach (var ip in batch) tried.Add(ip);

            var winner = await _racer.FetchAsync(_host, _port, isTls: true, headBytes, body, batch, ct);
            if (winner is not null) return (winner, tried.Count);
            if (ct.IsCancellationRequested) break;

            if (!_resolver.IsScanCompleted(_host))
                AppLog.Warn($"[MITM] {_host} 本批 {batch.Count} 个 IP 均不可用（累计已试 {tried.Count} 个），等 DNS 扫描补新 IP");
        }

        if (tried.Count == 0)
            AppLog.Warn($"[MITM] 解析失败: {_host} 加速池与系统 DNS 都扫完了，一个 IP 都没解析出来");
        else
            AppLog.Warn($"[MITM] 竞速失败: {_host} 共 {tried.Count} 个候选 IP 均不可用（DNS 已全部扫完）");
        return (null, tried.Count);
    }

    /// <summary>
    /// 允许「每个候选 IP 各发一份」广播竞速的条件：幂等方法（GET/HEAD/OPTIONS）**且没有请求体**。
    /// 带体的 GET 这类怪东西也走单发；未知/扩展方法一律按不安全处理（保守方向＝单发）。
    /// 注意这并非零风险：不规范的「GET 有副作用」接口（计数、一次性链接兑换）仍会被复制 N 份——
    /// 这是保留「谁先返回响应头谁赢」加速收益的代价。
    /// </summary>
    private static bool IsFanoutSafe(string method, byte[]? body) =>
        body is null or { Length: 0 } &&
        (method.Equals("GET", StringComparison.OrdinalIgnoreCase)
         || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
         || method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 单发模式（非幂等方法或任何带请求体的请求）：一批候选**并发做 TCP+TLS 握手**（一字节请求都不写，
    /// 见 <see cref="UpstreamRacer.RaceConnectTlsAsync"/>），握手赢家拿到之后才把请求发**一次**，
    /// 然后在这一条上等响应头（预算 <see cref="ProxyOptions.MitmResponseHeadTimeoutMs"/>）。
    /// 关键不变量：**请求字节一旦交出就不再换 IP、不再重赛**——远端可能已经执行了，重发＝重复执行；
    /// 只有「整批都没握上手」（字节必然未发出）才继续下一批或按 RaceRetryRounds 额度重赛。
    /// 失败与广播模式同规格：一个字节都不回，直接结束会话（浏览器 ERR_EMPTY_RESPONSE）。
    /// </summary>
    private async Task<bool> ServeSingleSendAsync(HttpHead reqHead, byte[] headBytes, byte[]? body, CancellationToken ct)
    {
        var tried = new HashSet<IPAddress>();
        var retriesUsed = 0;

        // 取 IP 的节奏与广播模式一致：本连接没试过的批次 →（已知批次全灭后）重赛
        async Task<IReadOnlyList<IPAddress>?> NextBatchAsync()
        {
            var fresh = await _resolver.WaitForNewIpsAsync(_host, tried, ct).ConfigureAwait(false);
            if (fresh is not null) return fresh;
            if (tried.Count == 0)
                return null;   // 一个 IP 都没解析出来：重赛没有意义，交给 NegativeTtl 的下一次扫描

            var again = await _resolver.RetryKnownIpsAsync(
                _host, retriesUsed, _options.RaceRetryRounds, _options.RaceRetryDelayMs, ct).ConfigureAwait(false);
            if (again is null) return null;

            retriesUsed++;
            AppLog.Warn($"[MITM] {_host} 已知 {tried.Count} 个候选 IP 均未完成握手，第 {retriesUsed}/{_options.RaceRetryRounds} 轮重赛（单发模式）");
            return again;
        }

        while (await NextBatchAsync() is { } batch)
        {
            foreach (var ip in batch) tried.Add(ip);

            var winner = await _racer.RaceConnectTlsAsync(_host, _port, isTls: true, batch, ct).ConfigureAwait(false);
            if (winner is null)
            {
                // 整批连 TCP 都没通 / TLS 都没握上——请求字节必定没发出去，换下一批或重赛都是安全的
                if (ct.IsCancellationRequested) break;
                if (!_resolver.IsScanCompleted(_host))
                    AppLog.Warn($"[MITM] {_host} 本批 {batch.Count} 个 IP 均未完成握手（累计已试 {tried.Count} 个），等 DNS 扫描补新 IP");
                continue;
            }

            using (winner)
            {
                _stats.RecordWinner(_host, winner.Ip, winner.ElapsedMs);
                AppLog.Debug($"[MITM] {reqHead.Method} {_host} → 握手竞速胜出 {winner.Ip}" +
                             $"（{winner.ElapsedMs}ms），请求单发（累计候选 {tried.Count} 个 IP）");

                HttpHead? respHead;
                try
                {
                    await winner.Conn.WriteAsync(headBytes, ct).ConfigureAwait(false);
                    if (body is { Length: > 0 })
                        await winner.Conn.WriteAsync(body, ct).ConfigureAwait(false);
                    await winner.Conn.FlushAsync(ct).ConfigureAwait(false);

                    using var headWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    headWait.CancelAfter(_options.MitmResponseHeadTimeoutMs);
                    respHead = await winner.Conn.ReadHeadAsync(headWait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 响应头预算耗尽：字节已经交给这个 IP，成败无从分辨，绝不重发
                    _stats.AddError();
                    AppLog.Warn($"[MITM] 单发超时: {_host} → {winner.Ip} 在 " +
                                $"{_options.MitmResponseHeadTimeoutMs / 1000}s 内没返回响应头；请求已交给该 IP，不重发");
                    return false;
                }
                catch (OperationCanceledException)
                {
                    return false;   // 客户端已经断开，没什么可说的
                }
                catch (Exception ex)
                {
                    // 写失败/响应非法也一律按「已交给远端」处理：分不清它收没收到，保守起见不重发
                    _stats.AddError();
                    AppLog.Warn($"[MITM] 单发失败: {_host} → {winner.Ip} 握手后出错（{ex.GetType().Name} {ex.Message}），不重发");
                    return false;
                }

                if (respHead is null)
                {
                    _stats.AddError();
                    AppLog.Warn($"[MITM] 单发失败: {_host} → {winner.Ip} 在返回响应头前关闭连接，请求可能已被远端执行，不重发");
                    return false;
                }

                return await RelayUpstreamResponseAsync(
                    new UpstreamResponse
                    {
                        Ip = winner.Ip, Tcp = winner.Tcp, Stream = winner.Stream, Conn = winner.Conn,
                        Head = respHead, ElapsedMs = winner.ElapsedMs,
                    },
                    reqHead.Method, ct).ConfigureAwait(false);
            }
        }

        _stats.AddError();
        AppLog.Warn($"[MITM] 竞速失败: {_host} 共 {tried.Count} 个候选 IP 均无法建立可用连接（DNS 已全部扫完，请求未发出过）");
        return false;
    }

    /// <summary>请求体过大时的退化路径：同样从共享条目取 IP，每批挑能连上的流式转发请求并回传响应；取 IP 的节奏与 <see cref="RaceKnownIpsAsync"/> 一致（含重赛）。</summary>
    private async Task<bool> ServeSingleAsync(HttpHead reqHead, CancellationToken ct)
    {
        var headBytes = BuildUpstreamRequestHead(reqHead, deChunked: false, contentLength: null);
        var tried = new HashSet<IPAddress>();
        var retriesUsed = 0;

        async Task<IReadOnlyList<IPAddress>?> NextBatchAsync()
        {
            var fresh = await _resolver.WaitForNewIpsAsync(_host, tried, ct).ConfigureAwait(false);
            if (fresh is not null) return fresh;
            if (tried.Count == 0) return null;

            var again = await _resolver.RetryKnownIpsAsync(
                _host, retriesUsed, _options.RaceRetryRounds, _options.RaceRetryDelayMs, ct).ConfigureAwait(false);
            if (again is null) return null;

            retriesUsed++;
            AppLog.Warn($"[MITM] {_host} 已知 {tried.Count} 个候选 IP 均连不上，第 {retriesUsed}/{_options.RaceRetryRounds} 轮重赛（直连模式）");
            return again;
        }

        while (await NextBatchAsync() is { } batch)
        {
            foreach (var ip in batch) tried.Add(ip);

            var upstream = await OpenFirstAvailableAsync(batch, headBytes, ct);
            if (upstream is not null)
                return await RelaySingleRequestAsync(upstream, reqHead, ct);
            if (ct.IsCancellationRequested) break;

            if (!_resolver.IsScanCompleted(_host))
                AppLog.Warn($"[MITM] {_host} 本批 IP 均连不上（累计已试 {tried.Count} 个），等 DNS 扫描补新 IP");
        }

        _stats.AddError();
        // 同 ServeRacedAsync：不发任何响应，直接关这条会话（Chrome 侧表现为 ERR_EMPTY_RESPONSE）。
        return false;
    }

    /// <summary>本批候选 IP 里逐个尝试建连 + TLS + 发请求头（直连模式最多试 3 个），成功返回已就绪的连接。
    /// 串行的含义就是「头只发给了第一条握手成功的连接」——这也是安全的单发，与广播竞速相对。</summary>
    private async Task<RacedConnection?> OpenFirstAvailableAsync(IReadOnlyList<IPAddress> ips, byte[] headBytes, CancellationToken ct)
    {
        foreach (var ip in ips.Take(3))
        {
            var sw = Stopwatch.StartNew();
            var tcp = new TcpClient(ip.AddressFamily);
            Stream? stream = null;
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(_options.MitmConnectTimeoutMs);
                await tcp.ConnectAsync(ip, _port, connectCts.Token);
                tcp.NoDelay = true;
                stream = tcp.GetStream();

                var ssl = new System.Net.Security.SslStream(stream, leaveInnerStreamOpen: false);
                var opts = new System.Net.Security.SslClientAuthenticationOptions
                {
                    TargetHost = _host,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.None,
                };
                if (_options.UpstreamCertValidator is not null)
                    opts.RemoteCertificateValidationCallback = _options.UpstreamCertValidator;
                await ssl.AuthenticateAsClientAsync(opts, ct);
                stream = ssl;

                var conn = new Http1Connection(stream);
                await conn.WriteAsync(headBytes, ct);
                await conn.FlushAsync(ct);
                return new RacedConnection(ip, tcp, stream, conn, (int)sw.ElapsedMilliseconds);
            }
            catch
            {
                try { stream?.Dispose(); } catch { }
                try { tcp.Dispose(); } catch { }
                if (ct.IsCancellationRequested) break;  // 客户端已断开，不必再试剩下的 IP
            }
        }
        return null;
    }

    /// <summary>把请求体流式转发给已就绪的上游连接（退化路径：请求头已发出），再把它返回的响应回传给客户端。</summary>
    private async Task<bool> RelaySingleRequestAsync(RacedConnection upstream, HttpHead reqHead, CancellationToken ct)
    {
        try
        {
            if (reqHead.ContentLength is long cl && cl > 0)
                await _client.RelayFixedAsync(upstream.Stream, cl, ct);
            else if (reqHead.IsChunked)
                await _client.RelayChunkedAsync(upstream.Stream, ct);
            await upstream.Stream.FlushAsync(ct);

            var respHead = await upstream.Conn.ReadHeadAsync(ct);
            if (respHead is null) throw new IOException("上游未返回响应头");

            using var session = new UpstreamResponse
            {
                Ip = upstream.Ip, Tcp = upstream.Tcp, Stream = upstream.Stream, Conn = upstream.Conn,
                Head = respHead, ElapsedMs = upstream.ElapsedMs,
            };
            _stats.RecordWinner(_host, upstream.Ip, upstream.ElapsedMs);
            AppLog.Debug($"[MITM] {_host} 直连 {upstream.Ip}（{upstream.ElapsedMs}ms 建连，请求体流式转发）");
            return await RelayUpstreamResponseAsync(session, reqHead.Method, ct);
        }
        finally
        {
            try { upstream.Stream.Dispose(); } catch { }
            try { upstream.Tcp.Dispose(); } catch { }
        }
    }

    // ==================== 响应回传 ====================

    private async Task<bool> RelayUpstreamResponseAsync(UpstreamResponse upstream, string requestMethod, CancellationToken ct)
    {
        var head = upstream.Head;

        // 1xx 临时响应原样透传，继续读最终响应
        while (head.IsResponse && head.StatusCode >= 100 && head.StatusCode < 200 && head.StatusCode != 101)
        {
            await _client.WriteAsync(head.RawBytes, ct);
            head = await upstream.Conn.ReadHeadAsync(ct)
                ?? throw new IOException("上游在 1xx 后关闭连接");
        }

        var noBody = string.Equals(requestMethod, "HEAD", StringComparison.OrdinalIgnoreCase)
            || head.StatusCode is 204 or 304;
        var untilClose = !noBody && !head.IsChunked && head.ContentLength is null;

        var rewritten = RewriteResponseHead(head, close: untilClose || head.StatusCode == 101);
        await _client.WriteAsync(rewritten, ct);
        await _client.FlushAsync(ct);

        // 101 协议升级（WebSocket 等）：转原始双向泵
        if (head.StatusCode == 101)
        {
            var a = _client.RelayPumpAsync(upstream.Stream, ct);
            var b = PumpAsync(upstream.Stream, _client.Stream, ct);
            await Task.WhenAny(a, b);
            return false;
        }

        if (noBody) return true;

        if (head.ContentLength is long cl)
        {
            await upstream.Conn.RelayFixedAsync(_client.Stream, cl, ct);
            return true;
        }
        if (head.IsChunked)
        {
            await upstream.Conn.RelayChunkedAsync(_client.Stream, ct);
            return true;
        }

        await upstream.Conn.RelayPumpAsync(_client.Stream, ct);
        return false; // 长度未知，只能随上游关闭
    }

    /// <summary>重建响应头：去掉逐跳头，按需改写 Connection。</summary>
    internal static byte[] RewriteResponseHead(HttpHead head, bool close)
    {
        var sb = new StringBuilder(512);
        sb.Append(head.StartLine).Append("\r\n");
        foreach (var (name, value) in head.RawHeaders)
        {
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        sb.Append(close ? "Connection: close\r\n" : "Connection: keep-alive\r\n");
        sb.Append("\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>重建发往上游的请求头（origin-form、去逐跳头、Connection: close、按需去 chunk）。</summary>
    internal static byte[] BuildUpstreamRequestHead(HttpHead head, bool deChunked, long? contentLength)
    {
        var parts = head.StartLine.Split(' ');
        var method = parts.Length > 0 ? parts[0] : "GET";
        var target = parts.Length > 1 ? parts[1] : "/";
        var version = parts.Length > 2 ? parts[2] : "HTTP/1.1";

        var sb = new StringBuilder(512);
        sb.Append(method).Append(' ').Append(target).Append(' ').Append(version).Append("\r\n");

        var hasUpgrade = head.HasHeader("Upgrade");
        foreach (var (name, value) in head.RawHeaders)
        {
            if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase)) continue;
            if (!hasUpgrade && name.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (deChunked && name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (deChunked && name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        if (deChunked && contentLength is not null)
            sb.Append("Content-Length: ").Append(contentLength.Value).Append("\r\n");

        sb.Append(hasUpgrade ? "Connection: Upgrade\r\n" : "Connection: close\r\n");
        sb.Append("\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static async Task PumpAsync(Stream src, Stream dst, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        try
        {
            while (true)
            {
                int n = await src.ReadAsync(buffer, ct);
                if (n == 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                await dst.FlushAsync(ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 泵结束
        }
    }

}
