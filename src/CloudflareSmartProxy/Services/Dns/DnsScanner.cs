using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Services;

/// <summary>扫描进度。</summary>
public readonly record struct DnsScanProgress(int Completed, int Total, int Succeeded);

/// <summary>测速后的 DNS 条目（按延迟升序排列）。</summary>
public sealed record DnsSpeedEntry(IPAddress Server, string? Name, int LatencyMs)
{
    public override string ToString() => $"{Server} ({LatencyMs}ms)";
}

/// <summary>一个 IP 及能解析出它的 DNS 服务器列表（组内按延迟升序）。</summary>
public sealed record IpGroupResult(IPAddress Ip, IReadOnlyList<DnsSpeedEntry> Servers)
{
    public int FastestLatencyMs => Servers.Count == 0 ? int.MaxValue : Servers[0].LatencyMs;
}

/// <summary>一次完整扫描的结果报告。</summary>
public sealed record DnsScanReport(
    string Domain,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    int TotalServers,
    int SucceededServers,
    IReadOnlyList<IpGroupResult> Groups,              // IP → DNS 列表（组内按延迟升序）
    IReadOnlyList<DnsSpeedEntry> RankedServers)       // 全部可用 DNS，按延迟升序
{
    public TimeSpan Duration => FinishedUtc - StartedUtc;
}

/// <summary>
/// 手动触发的 DNS 并发扫描：用数百个备选 DNS 解析同一域名，
/// 汇总「IP → 能解析出该 IP 的 DNS 列表」，并发数受控以避免资源耗尽。
/// </summary>
public sealed class DnsScanner
{
    /// <summary>执行扫描。取消时抛 <see cref="OperationCanceledException"/>。</summary>
    public async Task<DnsScanReport> ScanAsync(
        string domain,
        IReadOnlyList<DnsServerEntry> servers,
        int concurrency,
        int timeoutMs,
        IProgress<DnsScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new ArgumentException("域名不能为空", nameof(domain));
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();

        var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 1024));
        var ok = new ConcurrentBag<(DnsServerEntry Srv, IReadOnlyList<IPAddress> Ips, int Latency)>();
        int completed = 0, succeeded = 0;

        var tasks = servers.Select(async srv =>
        {
            await gate.WaitAsync(ct);
            try
            {
                try
                {
                    var r = await UdpDnsClient.QueryAAsync(srv.Ip, domain, timeoutMs, ct);
                    ok.Add((srv, r.Ips, r.LatencyMs));
                    Interlocked.Increment(ref succeeded);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // 用户主动停止才向上传播
                }
                catch
                {
                    // 单个服务器超时/不可达/无记录：计为失败，不影响整体
                }
            }
            finally
            {
                gate.Release();
                var c = Interlocked.Increment(ref completed);
                progress?.Report(new DnsScanProgress(c, servers.Count, succeeded));
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        var flat = ok.SelectMany(x => x.Ips.Select(ip => (Ip: ip, x.Srv, x.Latency))).ToList();

        var groups = flat
            .GroupBy(x => x.Ip)
            .Select(g => new IpGroupResult(
                g.Key,
                (IReadOnlyList<DnsSpeedEntry>)g.Select(x => new DnsSpeedEntry(x.Srv.Ip, x.Srv.Name, x.Latency))
                    .OrderBy(e => e.LatencyMs)
                    .ThenBy(e => e.Server.ToString(), StringComparer.Ordinal)
                    .ToList()))
            .OrderBy(g => g.FastestLatencyMs)
            .ThenByDescending(g => g.Servers.Count)
            .ToList();

        var ranked = ok
            .Select(x => new DnsSpeedEntry(x.Srv.Ip, x.Srv.Name, x.Latency))
            .OrderBy(e => e.LatencyMs)
            .ThenBy(e => e.Server.ToString(), StringComparer.Ordinal)
            .ToList();

        return new DnsScanReport(
            domain,
            DateTime.UtcNow,
            DateTime.UtcNow,
            servers.Count,
            ok.Count,
            groups,
            ranked);
    }
}
