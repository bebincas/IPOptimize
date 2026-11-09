using CloudflareSmartProxy.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// CDN 官方 IP 网段来源基类：负责拉取/缓存某家 CDN 的前缀列表，并派生测速候选 IP。
/// 候选采样（BuildCandidates）、归属判断（Contains / HostMatchesAsync）与 12 小时缓存刷新
/// 全部沿用原 CloudflareRangeProvider 的实现，派生类只提供「网段从哪里来、怎么解析」。
/// 采样与过滤规则已经过验证，请勿修改。
/// </summary>
public abstract class CdnRangeProvider
{
    /// <summary>档位名，与 CdnProfile.Name 一致；同时用作 ProxyEndpoint.Cdn 归属与代理分流键。</summary>
    public string Name { get; }

    /// <summary>
    /// 「当前 CDN」卡片的网段来源地址（档位初始化时写入）。
    /// 非空时覆盖内置默认地址；Cloudflare 档例外——官方是 ips-v4/ips-v6 两个列表，不受单地址覆盖影响。
    /// </summary>
    public string? RangeSourceUrl { get; set; }

    protected LogService Log { get; }
    // 网段拉取的整体预算 20 秒（HttpClient 内置 Timeout；超时后 .NET 7+ 抛的是内层 TimeoutException 的 TaskCanceledException）。
    // RefreshAsync 区分「调用方令牌取消」（上抛，优化流程的手动取消语义）与「这种超时」（沿用现有网段，不拦代理启动）。
    protected HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(20) };

    private IReadOnlyList<IpNetwork> _networks;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private string? _refreshedForUrl;

    protected CdnRangeProvider(string name, LogService log, IEnumerable<string>? fallbackCidrs = null)
    {
        Name = name;
        Log = log;
        _networks = Parse(fallbackCidrs ?? Enumerable.Empty<string>());
    }

    public IReadOnlyList<IpNetwork> Networks => _networks;

    /// <summary>RangeSourceUrl 为空时使用的内置官方来源地址。</summary>
    protected abstract string DefaultRangeSourceUrl { get; }

    /// <summary>实际生效的来源地址（也用作刷新缓存键：地址变了就强制重新拉取）。</summary>
    protected string EffectiveRangeSourceUrl =>
        string.IsNullOrWhiteSpace(RangeSourceUrl) ? DefaultRangeSourceUrl : RangeSourceUrl.Trim();

    /// <summary>拉取并解析本 CDN 的网段列表；解析数量应做合理性校验，异常时抛出以保留现有网段。</summary>
    protected abstract Task<IReadOnlyList<IpNetwork>> FetchNetworksAsync(CancellationToken cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var url = EffectiveRangeSourceUrl;
        if (_refreshedForUrl == url && DateTime.UtcNow - _lastRefreshUtc < TimeSpan.FromHours(12))
            return;

        try
        {
            var parsed = await FetchNetworksAsync(cancellationToken);
            _networks = parsed;
            _lastRefreshUtc = DateTime.UtcNow;
            _refreshedForUrl = url;
            Log.Info($"{Name} 网段已更新：{parsed.Count} 个前缀。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 只有「调用方明确取消」才继续上抛：优化流程的手动取消（EdgeOptimizerV1.OptimizeAsync 传真令牌）依赖这个语义。
            throw;
        }
        catch (Exception ex)
        {
            // 其余一律按「这次更新没成功，沿用现有网段」处理：连接失败、数据非法，也包括 HttpClient Timeout 造成的
            // TaskCanceledException（旧 filter `when (ex is not OperationCanceledException)` 把这种超时也当手动取消放行，
            // 「通而慢」会拦腰中止代理启动——那句 "The request was canceled due to the configured HttpClient.Timeout
            // of 20 seconds elapsing" 就是它，而彻底断网反而不报：HttpRequestException 是能捕获的）。
            bool timedOut = ex is OperationCanceledException;   // 令牌没取消还能是 OCE ⇒ 只能是 HttpClient 超时
            if (!timedOut)
            {
                // 硬失败推进 12 小时缓存：每次启动都去撞一个不通的来源并不会更快；
                // 「网段来源」地址改动时 _refreshedForUrl 对不上，仍会立刻重试。
                // 超时则刻意不推进：「卡住」多半是瞬态（干扰、VPN/代理切换），下次启动/优选要能马上再试。
                _lastRefreshUtc = DateTime.UtcNow;
                _refreshedForUrl = url;
            }
            var reason = timedOut
                ? $"拉取超时（{Http.Timeout.TotalSeconds:0}s）"
                : ex.InnerException is { } inner ? $"{ex.Message}（{inner.Message}）" : ex.Message;
            Log.Warn(_networks.Count > 0
                ? $"无法更新 {Name} 官方网段，沿用现有网段列表：{reason}"
                : $"无法更新 {Name} 官方网段，当前没有可用网段：{reason}");
        }
    }

    public bool Contains(IPAddress address) => _networks.Any(n => n.Contains(address));

    public async Task<bool> HostMatchesAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
            return Contains(literal);

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            return addresses.Any(Contains);
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<string> BuildCandidates(int samplesPerPrefix)
    {
        // 使用与 CdnIPSpeedTester.GenerateIpList 一致的采样逻辑：按网段逐 /24 采样（/32 直接加入）。
        var list = new List<string>(1024);
        var rng = new Random(Environment.TickCount);

        foreach (var n in _networks)
        {
            var cidr = $"{n.NetworkAddress}/{n.PrefixLength}";
            // 根据地址族选择不同的采样逻辑
            if (n.NetworkAddress.AddressFamily == AddressFamily.InterNetwork)
                AddIPv4Range(list, cidr, rng, samplesPerPrefix);
            else if (n.NetworkAddress.AddressFamily == AddressFamily.InterNetworkV6)
                AddIPv6Range(list, cidr, rng, samplesPerPrefix);
        }
        //随机抽取1000
        if (list.Count > 1000)
        {
            var random = new Random();
            list = list.OrderBy(x => random.Next()).Take(1000).ToList();
        }
        return list
            .Distinct()
            .ToArray();
    }

    /// <summary>生成 IPv4 候选：/32 直接加入；否则逐 /24 采样（随机 1 个）。与 Go 逻辑一致（来源 CdnIPSpeedTester）。</summary>
    private static void AddIPv4Range(List<string> list, string cidr, Random rng, int samplesPerPrefix)
    {
        int slash = cidr.IndexOf('/');
        string ipPart = slash < 0 ? cidr : cidr.Substring(0, slash);
        int prefix = slash < 0 ? 32 : int.Parse(cidr.Substring(slash + 1), CultureInfo.InvariantCulture);
        if (prefix < 0 || prefix > 32) throw new FormatException($"非法的 IPv4 前缀: {cidr}");
        var addr = IPAddress.Parse(ipPart);
        if (addr.AddressFamily != AddressFamily.InterNetwork) return;

        var b = addr.GetAddressBytes();
        if (prefix == 32)
        {
            list.Add(FormatIpv4(b[0], b[1], b[2], b[3]));
            return;
        }

        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        uint ip = (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]) & mask; // 网络地址
        uint network = ip;
        uint minIp = ip & 0xFF;                                  // 第四段最小值
        long inv = ~mask;                                        // 反掩码
        int hosts = inv > 255 ? 255 : (int)inv;                  // 第四段可用主机数（上限 255）

        while ((ip & mask) == network)
        {
            uint baseIp = ip & 0xFFFFFF00u;
            int samples = Math.Max(1, samplesPerPrefix);
            for (int s = 0; s < samples; s++)
            {
                int r = hosts == 0 ? 0 : rng.Next(hosts); // 0..hosts-1
                list.Add(FormatIpv4FromUint(baseIp | (minIp + (uint)r)));
            }
            ip += 0x00000100u; // 进入下一个 /24（第三字节 +1）
        }
    }

    /// <summary>生成 IPv6 候选：/128 直接加入；否则在网段内随机游走采样。与 Go 逻辑一致。</summary>
    private static void AddIPv6Range(List<string> list, string cidr, Random rng, int samplesPerPrefix)
    {
        int slash = cidr.IndexOf('/');
        string ipPart = slash < 0 ? cidr : cidr.Substring(0, slash);
        int prefix = slash < 0 ? 128 : int.Parse(cidr.Substring(slash + 1), CultureInfo.InvariantCulture);
        if (prefix < 0 || prefix > 128) throw new FormatException($"非法的 IPv6 前缀: {cidr}");
        var addr = IPAddress.Parse(ipPart);
        if (addr.AddressFamily != AddressFamily.InterNetworkV6) return;

        var ip = addr.GetAddressBytes();
        if (prefix == 128)
        {
            list.Add(new IPAddress(ip).ToString());
            return;
        }

        var mask = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            int bits = prefix - i * 8;
            mask[i] = bits >= 8 ? (byte)255 : bits <= 0 ? (byte)0 : (byte)(0xFF << (8 - bits));
        }
        for (int i = 0; i < 16; i++) ip[i] &= mask[i];
        var network = (byte[])ip.Clone();

        while (InRange(ip, mask, network))
        {
            int samples = Math.Max(1, samplesPerPrefix);
            for (int s = 0; s < samples; s++)
            {
                ip[15] = (byte)rng.Next(256); // 随机最后一段
                ip[14] = (byte)rng.Next(256);
                var copy = (byte[])ip.Clone();
                list.Add(new IPAddress(copy).ToString());
            }
            for (int i = 13; i >= 0; i--) // 从倒数第三位往前随机游走
            {
                byte temp = ip[i];
                ip[i] += (byte)rng.Next(256);
                if (ip[i] >= temp) break;
            }
        }
    }

    private static bool InRange(byte[] ip, byte[] mask, byte[] network)
    {
        for (int i = 0; i < 16; i++)
            if ((ip[i] & mask[i]) != network[i]) return false;
        return true;
    }

    private static string FormatIpv4(byte a, byte b, byte c, byte d) => $"{a}.{b}.{c}.{d}";

    private static string FormatIpv4FromUint(uint u) =>
        $"{u >> 24}.{(u >> 16) & 0xFF}.{(u >> 8) & 0xFF}.{u & 0xFF}";

    // ------------------------------------------------------------------ 派生类共用工具

    protected static IReadOnlyList<IpNetwork> Parse(IEnumerable<string> cidrs) => cidrs
        .Select(x => x.Trim())
        .Where(x => x.Length > 0 && IpNetwork.TryParse(x, out _))
        .Select(x => { IpNetwork.TryParse(x, out var network); return network!; })
        .ToArray();

    /// <summary>GET 当前生效来源并按 JSON 解析；调用方用完请 Dispose。</summary>
    protected async Task<JsonDocument> GetJsonAsync(CancellationToken cancellationToken)
    {
        var json = await Http.GetStringAsync(EffectiveRangeSourceUrl, cancellationToken);
        return JsonDocument.Parse(json);
    }

    /// <summary>把 root 下某个字符串数组（CIDR 列表）收进列表；属性缺失或类型不符时忽略。</summary>
    protected static void CollectStringArray(JsonElement root, string property, List<string> into)
    {
        if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var e in arr.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
                into.Add(s);
    }

    /// <summary>
    /// 在对象数组里收集前缀：service / region 传 null 表示不过滤，
    /// 否则要求对应字段存在且大小写不敏感相等（AWS / GCP 的公开前缀表都是这种结构）。
    /// </summary>
    protected static void CollectPrefixArray(JsonElement root, string arrayProperty, string prefixProperty,
        string? service, string? region, List<string> into)
    {
        if (!root.TryGetProperty(arrayProperty, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object)
                continue;
            if (service is not null)
            {
                if (!e.TryGetProperty("service", out var svc) || svc.ValueKind != JsonValueKind.String ||
                    !string.Equals(svc.GetString(), service, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            if (region is not null)
            {
                if (!e.TryGetProperty("region", out var reg) || reg.ValueKind != JsonValueKind.String ||
                    !string.Equals(reg.GetString(), region, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            if (e.TryGetProperty(prefixProperty, out var p) && p.ValueKind == JsonValueKind.String &&
                p.GetString() is { Length: > 0 } s)
                into.Add(s);
        }
    }

    /// <summary>去重 + 解析 + 数量合理性校验；低于预期视为数据异常，抛出后基类会保留现有网段。</summary>
    protected IReadOnlyList<IpNetwork> RequireNetworks(List<string> cidrs, int minCount)
    {
        var parsed = Parse(cidrs.Distinct(StringComparer.OrdinalIgnoreCase));
        if (parsed.Count < minCount)
            throw new InvalidDataException($"{Name} 网段列表异常（解析到 {parsed.Count} 个前缀，至少应有 {minCount} 个）");
        return parsed;
    }
}
