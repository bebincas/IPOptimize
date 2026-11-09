using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 代理行为参数（DNS 加速 / 竞速 / MITM 相关）。除 <see cref="UpstreamCertValidator"/> 外，
/// 全部由「设置」页驱动：MainWindow 持有这一个实例并原地更新（<c>ApplyDnsPoolToResolver</c>），
/// 同一个引用再交给 <see cref="ProxyServer"/>/<see cref="UpstreamRacer"/>/<see cref="MitmSession"/>，
/// 所以改完即时对运行中的代理生效，不需要重启代理。
/// </summary>
public sealed class ProxyOptions
{
    /// <summary>每批并发竞速的 IP 数；解析到的全部 IP 会被一批一批试完，不是总量上限。</summary>
    public int RaceMaxIps { get; set; } = 8;

    /// <summary>单个候选 IP 的 TCP 建连上限（ms）。注意与 <c>AppSettings.ConnectTimeoutMs</c>（测速用）不是一回事。</summary>
    public int RaceConnectTimeoutMs { get; set; } = 5000;

    /// <summary>一批并发竞速从建连到拿到响应头的总预算（ms）；每批各自享有。</summary>
    public int RaceBatchBudgetMs { get; set; } = 25000;

    /// <summary>请求体缓冲上限（字节）：超过则放弃竞速，退化为单连接流式转发。</summary>
    public int RequestBodyBufferLimitBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>MITM 逐请求竞速里单个候选的建连上限（ms）。</summary>
    public int MitmConnectTimeoutMs { get; set; } = 6000;

    /// <summary>
    /// 单发模式（非幂等方法 / 带请求体）里「握手胜出 → 把请求发给这一条 → 读到响应头」的响应头预算（ms，默认 60s）。
    /// 与 <see cref="RaceBatchBudgetMs"/> 分工：那个是「一批并发各发一份」的竞速预算，只适用于幂等方法的 fan-out；
    /// 单发只有一条连接在等远端，慢接口（导出、报表）很正常，所以给得更宽。超时＝请求字节**已交给远端**，
    /// 一律直接结束会话（浏览器 ERR_EMPTY_RESPONSE），绝不换 IP 重发——重发才是重复执行的来源。
    /// </summary>
    public int MitmResponseHeadTimeoutMs { get; set; } = 60_000;

    /// <summary>
    /// 已知候选 IP 全部试完仍无人应答时的「重赛」轮数（0＝立刻判死）。
    /// 重赛只是把同一批地址再并发试一次（候选从不被拉黑），治「刚才不行、过一会又行」的瞬时抖动。
    /// </summary>
    public int RaceRetryRounds { get; set; } = 2;

    /// <summary>每轮重赛前的等待（ms，默认 800）。</summary>
    public int RaceRetryDelayMs { get; set; } = 800;

    /// <summary>哪些 CONNECT 端口按 TLS 做 MITM（默认 443，可加 8443 等）。整体替换，读取侧每次现取。</summary>
    public HashSet<int> MitmPorts { get; set; } = new() { 443 };

    /// <summary>
    /// 上游证书校验回调。null = 默认严格校验（推荐，仅测试场景允许覆盖）。
    /// </summary>
    public RemoteCertificateValidationCallback? UpstreamCertValidator { get; set; }
}

/// <summary>一条上游连接及其已读到的响应头。</summary>
public sealed class UpstreamResponse : IDisposable
{
    public required IPAddress Ip { get; init; }
    public required TcpClient Tcp { get; init; }
    public required Stream Stream { get; init; }
    public required Http1Connection Conn { get; init; }
    public required HttpHead Head { get; init; }
    public int ElapsedMs { get; init; }

    public void Dispose()
    {
        try { Stream.Dispose(); } catch { }
        try { Tcp.Dispose(); } catch { }
    }
}

/// <summary>
/// 已就绪但**响应还没开始**的上游连接。两种来路：
/// <see cref="UpstreamRacer.RaceConnectTlsAsync"/> 的握手竞速赢家（一字节请求都未写入，调用方随后单发）；
/// <c>MitmSession.OpenFirstAvailableAsync</c> 的退化路径（请求头已发出、请求体待流式转发）。
/// 都是「只可能有一条连接真正承载这个请求」的形态，与「每个候选 IP 各发一份」的广播竞速相对。
/// </summary>
public sealed record RacedConnection(IPAddress Ip, TcpClient Tcp, Stream Stream, Http1Connection Conn, int ElapsedMs) : IDisposable
{
    public void Dispose()
    {
        try { Stream.Dispose(); } catch { }
        try { Tcp.Dispose(); } catch { }
    }
}

/// <summary>
/// 上游 IP 竞速：以 <see cref="ProxyOptions.RaceMaxIps"/> 为并发批次大小，一批一批把全部候选 IP 试完。
/// 每批并发连接（可选 TLS）+ 发请求 + 读响应头，第一个成功返回响应头的胜出，同批其余立即取消释放；
/// 整批都失败但还有未试的 IP 就继续下一批，全部 IP 都失败才返回 null。
/// </summary>
public sealed class UpstreamRacer
{
    public required ProxyOptions Options { get; init; }
    public required ProxyStatistics Stats { get; init; }

    /// <summary>
    /// 逐批竞速直到某个 IP 返回响应头或全部 IP 试完。
    /// 每批各自享有 <see cref="ProxyOptions.RaceBatchBudgetMs"/> 预算；调用方 <paramref name="ct"/> 取消则立即停止后续批次。
    /// </summary>
    public async Task<UpstreamResponse?> FetchAsync(
        string host,
        int port,
        bool isTls,
        byte[] requestHead,
        byte[]? requestBody,
        IReadOnlyList<IPAddress> ips,
        CancellationToken ct = default)
    {
        var candidates = ips.Distinct().ToArray();
        if (candidates.Length == 0) return null;

        Stats.AddRace();
        var batchSize = Math.Clamp(Options.RaceMaxIps, 1, 64);
        var attempted = 0;

        for (var offset = 0; offset < candidates.Length; offset += batchSize)
        {
            if (ct.IsCancellationRequested) break; // 客户端/代理已断开，不再消耗后续批次

            var batch = new IPAddress[Math.Min(batchSize, candidates.Length - offset)];
            Array.Copy(candidates, offset, batch, 0, batch.Length);

            var winner = await RaceBatchAsync(host, port, isTls, requestHead, requestBody, batch, ct);
            attempted += batch.Length;
            if (winner is not null) return winner;

            var remaining = candidates.Length - attempted;
            if (remaining > 0)
                AppLog.Warn($"竞速 {host}: 已试 {attempted}/{candidates.Length} 个 IP 均失败，继续下一批 {Math.Min(batchSize, remaining)} 个");
        }

        if (attempted == candidates.Length)
            AppLog.Warn($"竞速失败: {host} 全部 {attempted} 个候选 IP 均未返回响应头");
        else
            AppLog.Warn($"竞速中止: {host} 已试 {attempted}/{candidates.Length} 个 IP，连接已取消");
        return null;
    }

    /// <summary>一批并发竞速：首个响应头胜出，同批输家取消并后台释放；整批失败返回 null。</summary>
    private async Task<UpstreamResponse?> RaceBatchAsync(
        string host, int port, bool isTls,
        byte[] requestHead, byte[]? requestBody,
        IPAddress[] batch, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Options.RaceBatchBudgetMs);

        var pending = new List<Task<UpstreamResponse?>>(batch.Length);
        foreach (var ip in batch)
            pending.Add(TryFetchAsync(host, port, isTls, requestHead, requestBody, ip, cts.Token));

        UpstreamResponse? winner = null;
        try
        {
            while (pending.Count > 0 && winner is null)
            {
                var done = await Task.WhenAny(pending);
                pending.Remove(done);
                var r = await done; // TryFetchAsync 内部已兜底，不会抛
                if (r is not null) winner = r;
            }
        }
        finally
        {
            cts.Cancel(); // 输家立即取消
            foreach (var t in pending) _ = CleanupAsync(t);
        }
        return winner;
    }

    private static async Task CleanupAsync(Task<UpstreamResponse?> task)
    {
        try { (await task)?.Dispose(); }
        catch { /* 取消过程中的异常忽略 */ }
    }

    /// <summary>
    /// 透明隧道用的 TCP 竞速：一批一批并发连接候选 IP（不解密，只看谁先连上），最快建连者胜出，
    /// 同批其余立即取消并释放；整批失败继续下一批，全部失败返回 null。
    /// </summary>
    public async Task<TcpClient?> RaceTcpConnectAsync(
        IReadOnlyList<IPAddress> ips, int port, CancellationToken ct = default)
    {
        var candidates = ips.Distinct().ToArray();
        if (candidates.Length == 0) return null;

        Stats.AddRace();
        var batchSize = Math.Clamp(Options.RaceMaxIps, 1, 64);
        var attempted = 0;

        for (var offset = 0; offset < candidates.Length; offset += batchSize)
        {
            if (ct.IsCancellationRequested) break;

            var batch = new IPAddress[Math.Min(batchSize, candidates.Length - offset)];
            Array.Copy(candidates, offset, batch, 0, batch.Length);

            var winner = await RaceConnectBatchAsync(batch, port, ct);
            attempted += batch.Length;
            if (winner is not null) return winner;

            if (candidates.Length - attempted > 0)
                AppLog.Warn($"TCP 竞速: 已试 {attempted}/{candidates.Length} 个 IP 均失败，继续下一批");
        }
        return null;
    }

    private async Task<TcpClient?> RaceConnectBatchAsync(IPAddress[] batch, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Options.RaceConnectTimeoutMs);

        var pending = batch.Select(ip => ConnectOneAsync(ip, port, cts.Token)).ToList();
        TcpClient? winner = null;
        try
        {
            while (pending.Count > 0 && winner is null)
            {
                var done = await Task.WhenAny(pending);
                pending.Remove(done);
                var client = await done; // ConnectOneAsync 内部已兜底，不会抛
                if (client is not null) winner = client;
            }
        }
        finally
        {
            cts.Cancel(); // 输家立即取消
            foreach (var t in pending) _ = DisposeLoserAsync(t);
        }
        return winner;
    }

    private static async Task DisposeLoserAsync(Task<TcpClient?> task)
    {
        try { (await task)?.Dispose(); }
        catch { /* 取消过程中的异常忽略 */ }
    }

    private static async Task<TcpClient?> ConnectOneAsync(IPAddress ip, int port, CancellationToken ct)
    {
        var tcp = new TcpClient(ip.AddressFamily);
        try
        {
            await tcp.ConnectAsync(ip, port, ct);
            tcp.NoDelay = true;
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            return null;
        }
    }

    /// <summary>
    /// 「单发模式」用的握手竞速：一批一批并发做 TCP 连接 +（可选）TLS 握手，**一字节请求内容都不写**，
    /// 先握完的胜出，其余立即取消释放。治的是「逐请求竞速把 POST 原样发给每个候选 IP＝远端重复执行 N 次」
    /// ——取消输家救不回来，字节早就出去了。赢家连上之后由调用方把请求发**一次**。
    /// 与 <see cref="RaceTcpConnectAsync"/>（只竞 TCP，透明隧道用）的区别：这里连 TLS 握手一起竞，
    /// 且把已认证的流连同 <see cref="Http1Connection"/> 一起交出去（MITM 里 CONNECT 之后首个请求不该再串行补握手）。
    /// </summary>
    public async Task<RacedConnection?> RaceConnectTlsAsync(
        string host, int port, bool isTls, IReadOnlyList<IPAddress> ips, CancellationToken ct = default)
    {
        var candidates = ips.Distinct().ToArray();
        if (candidates.Length == 0) return null;

        Stats.AddRace();
        var batchSize = Math.Clamp(Options.RaceMaxIps, 1, 64);
        var attempted = 0;

        for (var offset = 0; offset < candidates.Length; offset += batchSize)
        {
            if (ct.IsCancellationRequested) break;

            var batch = new IPAddress[Math.Min(batchSize, candidates.Length - offset)];
            Array.Copy(candidates, offset, batch, 0, batch.Length);

            var winner = await RaceHandshakeBatchAsync(host, port, isTls, batch, ct);
            attempted += batch.Length;
            if (winner is not null) return winner;

            if (candidates.Length - attempted > 0)
                AppLog.Warn($"握手竞速 {host}: 已试 {attempted}/{candidates.Length} 个 IP 均未完成握手，继续下一批");
        }
        return null;
    }

    /// <summary>一批并发握手：首个完成者胜出；整批都没握上返回 null（此时请求字节必定未发出，调用方可安全换批/重赛）。</summary>
    private async Task<RacedConnection?> RaceHandshakeBatchAsync(
        string host, int port, bool isTls, IPAddress[] batch, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Options.MitmConnectTimeoutMs);   // TCP+TLS 整体预算（与退化路径 OpenFirstAvailableAsync 同一参数）

        var pending = new List<Task<RacedConnection?>>(batch.Length);
        foreach (var ip in batch)
            pending.Add(TryHandshakeAsync(host, port, isTls, ip, cts.Token));

        RacedConnection? winner = null;
        try
        {
            while (pending.Count > 0 && winner is null)
            {
                var done = await Task.WhenAny(pending);
                pending.Remove(done);
                var r = await done; // TryHandshakeAsync 内部已兜底，不会抛
                if (r is not null) winner = r;
            }
        }
        finally
        {
            cts.Cancel(); // 输家立即取消
            foreach (var t in pending) _ = DisposeLoserAsync(t);
        }
        return winner;
    }

    private static async Task DisposeLoserAsync(Task<RacedConnection?> task)
    {
        try { (await task)?.Dispose(); }
        catch { /* 取消过程中的异常忽略 */ }
    }

    /// <summary>单个候选：连接 → TLS。任何失败释放并返回 null；**不碰请求字节**。</summary>
    private async Task<RacedConnection?> TryHandshakeAsync(
        string host, int port, bool isTls, IPAddress ip, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        TcpClient? tcp = null;
        Stream? stream = null;
        try
        {
            tcp = new TcpClient(ip.AddressFamily);
            await tcp.ConnectAsync(ip, port, ct);
            tcp.NoDelay = true;
            stream = tcp.GetStream();

            if (isTls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                var sslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = host,          // SNI + 证书按域名校验
                    EnabledSslProtocols = SslProtocols.None,
                };
                if (Options.UpstreamCertValidator is not null)
                    sslOptions.RemoteCertificateValidationCallback = Options.UpstreamCertValidator;
                await ssl.AuthenticateAsClientAsync(sslOptions, ct);
                stream = ssl;
            }

            return new RacedConnection(ip, tcp, stream, new Http1Connection(stream), (int)sw.ElapsedMilliseconds);
        }
        catch
        {
            try { stream?.Dispose(); } catch { }
            try { tcp?.Dispose(); } catch { }
            return null;
        }
    }

    /// <summary>单个候选：连接 →（TLS）→ 发请求 → 读响应头。任何失败返回 null。</summary>
    public async Task<UpstreamResponse?> TryFetchAsync(
        string host, int port, bool isTls,
        byte[] requestHead, byte[]? requestBody,
        IPAddress ip, CancellationToken ct)
    {
        TcpClient? tcp = null;
        Stream? stream = null;
        try
        {
            var sw = Stopwatch.StartNew();

            tcp = new TcpClient(ip.AddressFamily);
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(Options.RaceConnectTimeoutMs);
                await tcp.ConnectAsync(ip, port, connectCts.Token);
            }
            tcp.NoDelay = true;
            stream = tcp.GetStream();

            if (isTls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                var sslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = host,          // SNI + 证书按域名校验
                    EnabledSslProtocols = SslProtocols.None,
                };
                if (Options.UpstreamCertValidator is not null)
                    sslOptions.RemoteCertificateValidationCallback = Options.UpstreamCertValidator;
                await ssl.AuthenticateAsClientAsync(sslOptions, ct);
                stream = ssl;
            }

            var conn = new Http1Connection(stream);
            await conn.WriteAsync(requestHead, ct);
            if (requestBody is { Length: > 0 })
                await conn.WriteAsync(requestBody, ct);
            await conn.FlushAsync(ct);

            var head = await conn.ReadHeadAsync(ct);
            if (head is null)
                throw new IOException("上游未返回响应头");

            return new UpstreamResponse
            {
                Ip = ip, Tcp = tcp, Stream = stream, Conn = conn,
                Head = head, ElapsedMs = (int)sw.ElapsedMilliseconds,
            };
        }
        catch
        {
            try { stream?.Dispose(); } catch { }
            try { tcp?.Dispose(); } catch { }
            return null;
        }
    }
}
