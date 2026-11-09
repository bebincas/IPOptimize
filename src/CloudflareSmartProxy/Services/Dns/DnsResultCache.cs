using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Services;

/// <summary>缓存条目（供 UI 展示）。</summary>
public sealed record DnsCacheEntryView(string Domain, IReadOnlyList<IPAddress> Ips, DateTime ExpiresAtUtc, bool ScanCompleted);

/// <summary>
/// 域名 → IP 列表本地缓存，外加「这个域名的 DNS 池是否已经全部扫完」的标识。
/// TTL 过期自动清理（后台定时扫描）；标识只在条目存活期内有效，条目一过期就当作没扫过，下次访问重新扫描。
/// 另外每个条目带一个「有变化就拨一次」的等待信号：追加 IP / 标记扫完 / 条目被清理时都会拨，
/// 等待新 IP 的连接不用轮询（见 <see cref="TrySnapshot"/> 与 <c>ResolverHub.WaitForNewIpsAsync</c>）。
/// </summary>
public sealed class DnsResultCache : IDisposable
{
    private sealed class Entry
    {
        public readonly object Sync = new();
        public IReadOnlyList<IPAddress> Ips = Array.Empty<IPAddress>();
        public DateTime ExpiresAtUtc;
        public bool ScanCompleted;
        public TaskCompletionSource<bool> Changed = NewChanged();

        private static TaskCompletionSource<bool> NewChanged() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAlive => ExpiresAtUtc > DateTime.UtcNow;

        /// <summary>唤醒所有等待者：完成旧的等待任务并换一个新的。</summary>
        public void Pulse()
        {
            var old = Changed;
            Changed = NewChanged();
            old.TrySetResult(true);
        }
    }

    private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _purgeTimer;

    public DnsResultCache(TimeSpan? purgeInterval = null)
    {
        _purgeTimer = new Timer(_ => PurgeExpired(), null,
            purgeInterval ?? TimeSpan.FromSeconds(30),
            purgeInterval ?? TimeSpan.FromSeconds(30));
    }

    /// <summary>条目被后台清理时触发（供 UI 打日志），在 Timer 线程上调用。</summary>
    public event Action<string>? Expired;

    public int Count => _map.Count;

    public bool TryGet(string host, out IReadOnlyList<IPAddress> ips)
    {
        ips = Array.Empty<IPAddress>();
        if (!_map.TryGetValue(host, out var e)) return false;
        lock (e.Sync)
        {
            if (!e.IsAlive)
            {
                _map.TryRemove(host, out _);
                return false;
            }
            ips = e.Ips;
            return true;
        }
    }

    /// <summary>该域名的 DNS 池是否已经全部扫完（条目过期/不存在都算没扫完）。</summary>
    public bool IsScanCompleted(string host)
    {
        if (!_map.TryGetValue(host, out var e)) return false;
        lock (e.Sync) return e.ScanCompleted && e.IsAlive;
    }

    /// <summary>当前缓存里该域名已知 IP 的个数（不存在的条目＝0）。</summary>
    public int IpCount(string host)
    {
        if (!_map.TryGetValue(host, out var e)) return 0;
        lock (e.Sync) return e.IsAlive ? e.Ips.Count : 0;
    }

    public void Set(string host, IEnumerable<IPAddress> ips, TimeSpan ttl)
    {
        var list = ips.Distinct().ToArray();
        if (list.Length == 0) return;
        var e = GetOrCreate(host, ttl);
        lock (e.Sync)
        {
            e.Ips = list;
            e.ExpiresAtUtc = LaterOf(e.ExpiresAtUtc, DateTime.UtcNow + ttl);
            e.Pulse();
        }
    }

    /// <summary>
    /// 先把条目建起来（还没有扫描结果时也要有个「正在扫」的占位），过期时间取 <paramref name="ttl"/>。
    /// 已有的存活条目不动它的时间，免得把更长 TTL 的解析结果压短。
    /// </summary>
    public void Touch(string host, TimeSpan ttl)
    {
        var e = GetOrCreate(host, ttl);
        lock (e.Sync)
        {
            if (!e.IsAlive) e.ExpiresAtUtc = DateTime.UtcNow + ttl;
        }
    }

    /// <summary>
    /// 把新解析到的 IP 追加进条目（顺序追加、跳过已有），并把过期时间顺延；返回真正新增的个数。
    /// 这里刻意不提供「按竞速失败删 IP」的入口：连不上多半是瞬时的（1 秒连不上不代表 10 秒后还连不上），
    /// 地址要留在缓存里，已试过的 IP 只在单条连接内记账。
    /// </summary>
    public int Append(string host, IEnumerable<IPAddress> ips, TimeSpan ttl)
    {
        var incoming = ips.Distinct().ToArray();
        if (incoming.Length == 0) return 0;

        var e = GetOrCreate(host, ttl);
        lock (e.Sync)
        {
            var merged = new List<IPAddress>(e.Ips);
            var added = 0;
            foreach (var ip in incoming)
                if (!merged.Contains(ip)) { merged.Add(ip); added++; }

            e.Ips = merged;
            e.ExpiresAtUtc = LaterOf(e.ExpiresAtUtc, DateTime.UtcNow + ttl);
            if (added > 0) e.Pulse();
            return added;
        }
    }

    /// <summary>标记「这个域名的 DNS 池已经全部扫完」并唤醒等待者；条目不存在时按 <paramref name="ttl"/> 建一个空条目。</summary>
    public void MarkScanCompleted(string host, TimeSpan ttl)
    {
        var e = GetOrCreate(host, ttl);
        lock (e.Sync)
        {
            e.ScanCompleted = true;
            if (!e.IsAlive) e.ExpiresAtUtc = DateTime.UtcNow + ttl;
            e.Pulse();
        }
    }

    /// <summary>
    /// 一次锁内快照：当前 IP 列表、是否已扫完、以及「下一次变化」的等待任务。
    /// 三者同锁取出，保证「先看列表再等信号」不会漏掉中间的变化。
    /// </summary>
    public bool TrySnapshot(string host, out IReadOnlyList<IPAddress> ips, out bool scanCompleted, out Task changeSignal)
    {
        ips = Array.Empty<IPAddress>();
        scanCompleted = false;
        changeSignal = Task.CompletedTask;
        if (!_map.TryGetValue(host, out var e)) return false;
        lock (e.Sync)
        {
            if (!e.IsAlive) return false;
            ips = e.Ips;
            scanCompleted = e.ScanCompleted;
            changeSignal = e.Changed.Task;
            return true;
        }
    }

    public bool Remove(string host)
    {
        if (!_map.TryRemove(host, out var e)) return false;
        lock (e.Sync) e.Pulse();
        return true;
    }

    public void Clear()
    {
        var snapshot = _map.ToArray();
        _map.Clear();
        foreach (var kv in snapshot)
            lock (kv.Value.Sync) kv.Value.Pulse();
    }

    public IReadOnlyList<DnsCacheEntryView> Snapshot() =>
        _map.Select(kv =>
        {
            lock (kv.Value.Sync)
                return new DnsCacheEntryView(kv.Key, kv.Value.Ips, kv.Value.ExpiresAtUtc, kv.Value.ScanCompleted);
        })
        .OrderBy(x => x.Domain, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public void PurgeExpired()
    {
        foreach (var kv in _map)
            if (kv.Value.ExpiresAtUtc <= DateTime.UtcNow && _map.TryRemove(kv.Key, out var e))
            {
                lock (e.Sync) e.Pulse();      // 别让正在等变化的人干等到超时
                Expired?.Invoke(kv.Key);
            }
    }

    private Entry GetOrCreate(string host, TimeSpan ttl)
    {
        var e = _map.GetOrAdd(host, _ => new Entry { ExpiresAtUtc = DateTime.UtcNow + ttl });
        return e;
    }

    private static DateTime LaterOf(DateTime a, DateTime b) => a > b ? a : b;

    public void Dispose() => _purgeTimer.Dispose();
}
