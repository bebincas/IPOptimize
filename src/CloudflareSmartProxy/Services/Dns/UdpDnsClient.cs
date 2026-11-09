using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Services;

/// <summary>一次查询结果（成功时）。</summary>
public sealed record UdpDnsResult(System.Collections.Generic.IReadOnlyList<IPAddress> Ips, uint TtlSeconds, int LatencyMs);

/// <summary>
/// 对指定 DNS 服务器发起一次 UDP A 查询，带超时与来源校验。
/// 超时用 <see cref="Task.WhenAny"/> 与 <see cref="Task.Delay(int)"/> 竞速实现，
/// 并给接收 Task 挂一个吞异常的续体，避免超时后出现 UnobservedTaskException。
/// </summary>
public static class UdpDnsClient
{
    public static async Task<UdpDnsResult> QueryAAsync(
        IPAddress server, string domain, int timeoutMs, CancellationToken ct = default)
    {
        var queryId = DnsMessage.RandomTransactionId();
        var query = DnsMessage.BuildQueryA(queryId, domain);
        var endpoint = new IPEndPoint(server, 53);

        using var udp = new UdpClient();
        var sw = Stopwatch.StartNew();

        await udp.SendAsync(query, endpoint, ct);
        ct.ThrowIfCancellationRequested();

        Task<UdpReceiveResult> receiveTask = udp.ReceiveAsync(ct).AsTask();
        // 防止超时后观察不到异常导致 UnobservedTaskException
        _ = receiveTask.ContinueWith(
            static t => { var _ = t.Exception; },
            TaskContinuationOptions.ExecuteSynchronously);

        var timeoutTask = Task.Delay(timeoutMs, ct);
        var done = await Task.WhenAny(receiveTask, timeoutTask);
        if (done != receiveTask)
            throw new TimeoutException($"DNS {server} 查询超时({timeoutMs}ms)");

        var result = await receiveTask;
        sw.Stop();

        if (!result.RemoteEndPoint.Address.Equals(server))
            throw new InvalidDataException($"DNS 应答来源 {result.RemoteEndPoint} 与目标 {server} 不一致");

        var answer = DnsMessage.ParseAResponse(result.Buffer, queryId);
        if (answer.Ips.Count == 0)
            throw new InvalidDataException($"DNS {server} 未返回 A 记录");

        return new UdpDnsResult(answer.Ips, answer.TtlSeconds, (int)sw.ElapsedMilliseconds);
    }
}
