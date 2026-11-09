using CloudflareSmartProxy.Models;
using CloudflareSmartProxy.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// Cloudflare 网段来源：官方 ips-v4 / ips-v6 两个纯文本 CIDR 列表，拉取失败时回退内置列表。
/// 候选采样与归属判断继承自 CdnRangeProvider（与历史实现完全一致）。
/// 归属判定优先级最高，必须保持在所有档位之前（见 CdnRangeProviders.CreateAll 的顺序约定）。
/// </summary>
public sealed class CloudflareRangeProvider : CdnRangeProvider
{
    private static readonly string[] FallbackCidrs =
    {
        "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "104.16.0.0/13",
        "104.24.0.0/14", "108.162.192.0/18", "131.0.72.0/22", "141.101.64.0/18",
        "162.158.0.0/15", "172.64.0.0/13", "173.245.48.0/20", "188.114.96.0/20",
        "190.93.240.0/20", "197.234.240.0/22", "198.41.128.0/17",
        "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32",
        "2405:8100::/32", "2a06:98c0::/29", "2c0f:f248::/32"
    };

    public CloudflareRangeProvider(LogService log)
        : base("Cloudflare", log, FallbackCidrs)
    {
    }

    // 官方来源是 ips-v4/ips-v6 两个列表，卡片的单地址覆盖（RangeSourceUrl）对 Cloudflare 不生效。
    protected override string DefaultRangeSourceUrl => "https://www.cloudflare.com/ips-v4";

    protected override async Task<IReadOnlyList<IpNetwork>> FetchNetworksAsync(CancellationToken cancellationToken)
    {
        var v4Task = Http.GetStringAsync("https://www.cloudflare.com/ips-v4", cancellationToken);
        var v6Task = Http.GetStringAsync("https://www.cloudflare.com/ips-v6", cancellationToken);
        await Task.WhenAll(v4Task, v6Task);
        var lines = (await v4Task).Split('\n').Concat((await v6Task).Split('\n'));
        var parsed = Parse(lines);
        if (parsed.Count < 10)
            throw new InvalidDataException("官方网段列表内容异常");
        return parsed;
    }
}

/// <summary>
/// Amazon CloudFront 网段来源：GET https://ip-ranges.amazonaws.com/ip-ranges.json。
/// 只提取 service=CLOUDFRONT 且 region=GLOBAL 的客户端边缘段（v4 在 prefixes[].ip_prefix，
/// v6 在 ipv6_prefixes[].ipv6_prefix）。按区域列出的 CLOUDFRONT 是区域边缘缓存（REC），
/// CLOUDFRONT_ORIGIN_FACING 是回源段，均不面向客户端，不参与优选。
/// </summary>
public sealed class AmazonCloudFrontRangeProvider : CdnRangeProvider
{
    public AmazonCloudFrontRangeProvider(LogService log, string? rangeSourceUrl = null)
        : base("Amazon CloudFront", log)
    {
        RangeSourceUrl = rangeSourceUrl;
    }

    protected override string DefaultRangeSourceUrl => "https://ip-ranges.amazonaws.com/ip-ranges.json";

    protected override async Task<IReadOnlyList<IpNetwork>> FetchNetworksAsync(CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync(cancellationToken);
        var root = doc.RootElement;
        var raw = new List<string>();
        CollectPrefixArray(root, "prefixes", "ip_prefix", "CLOUDFRONT", "GLOBAL", raw);
        CollectPrefixArray(root, "ipv6_prefixes", "ipv6_prefix", "CLOUDFRONT", "GLOBAL", raw);
        return RequireNetworks(raw, minCount: 10);
    }
}

/// <summary>
/// 档位名 → 网段来源。名字与 CdnProfile.Name 一致（大小写不敏感）；
/// 生成顺序即代理分流时归属判定的优先级——Cloudflare 必须最先，保持原有分流行为不变。
/// </summary>
public static class CdnRangeProviders
{
    public static List<CdnRangeProvider> CreateAll(IEnumerable<CdnProfile> profiles, LogService log)
    {
        var list = new List<CdnRangeProvider>();
        foreach (var profile in profiles)
        {
            var provider = CreateFor(profile, log);
            if (provider is not null &&
                !list.Any(x => string.Equals(x.Name, provider.Name, StringComparison.OrdinalIgnoreCase)))
                list.Add(provider);
        }
        return list;
    }

    private static CdnRangeProvider? CreateFor(CdnProfile profile, LogService log)
    {
        if (Is(profile, "Cloudflare")) return new CloudflareRangeProvider(log);
        if (Is(profile, "Amazon CloudFront")) return new AmazonCloudFrontRangeProvider(log, profile.RangeSource);
        return null; // 尚未接入的档位：不出现在测速/分流来源里
    }

    private static bool Is(CdnProfile profile, string name) =>
        string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase);
}
