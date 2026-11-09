using System;
using System.Linq;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 情况2 GlobalDns 加速白名单：逗号 / 分号 / 顿号 / 空格 / 换行分隔的域名条目，支持 * 通配符。
/// * 匹配任意长度字符（含点号），如 *.example.com 匹配 a.example.com 与 a.b.example.com；
/// 不含 * 的条目精确匹配自身（example.com 不匹配 www.example.com）；单独的 * 匹配全部。
/// 大小写不敏感；空清单 = 永不为真（即不启用全局解析加速）。
/// 解析后不可变、可并发读取，UI 保存时整体替换引用即可热更新。
/// </summary>
public sealed class DomainWildcardList
{
    private static readonly char[] Separators =
    {
        ',', '，', ';', '；', '、', ' ', '\t', '\r', '\n'
    };

    private readonly string[] _patterns;

    private DomainWildcardList(string[] patterns) => _patterns = patterns;

    public static DomainWildcardList Parse(string? raw) =>
        new(NormalizeEntries(raw).ToArray());

    /// <summary>
    /// 把原始文本拆成归一化后的条目：按同一套分隔符切分、去尾点、转小写、去重（保持首次出现的顺序）。
    /// 白名单合并（<see cref="DnsWhitelistDefaults.Merge"/>）与 Parse 共用这套规则，两边语义不会漂。
    /// </summary>
    public static IReadOnlyList<string> NormalizeEntries(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var piece in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = piece.Trim().TrimEnd('.').ToLowerInvariant(); // 条目也做尾点归一：example.com. == example.com
            if (entry.Length > 0 && seen.Add(entry)) list.Add(entry);
        }
        return list;
    }

    public bool IsEmpty => _patterns.Length == 0;

    /// <summary>去重归一后的条目数（0 = 未启用全局解析加速）。供 UI 保存提示用。</summary>
    public int Count => _patterns.Length;

    /// <summary>host 是否命中白名单（空清单或空 host 一律 false）。</summary>
    public bool IsMatch(string? host)
    {
        if (_patterns.Length == 0 || string.IsNullOrWhiteSpace(host))
            return false;

        var text = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (text.Length == 0)
            return false;

        foreach (var pattern in _patterns)
            if (WildcardMatch(pattern, text))
                return true;
        return false;
    }

    /// <summary>经典线性回溯的通配匹配：* 匹配任意字符序列（含点号与连字符）。</summary>
    private static bool WildcardMatch(string pattern, string text)
    {
        int p = 0, t = 0, starAt = -1, nextText = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == text[t])
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starAt = p++;
                nextText = t;
            }
            else if (starAt >= 0)
            {
                p = starAt + 1;
                t = ++nextText;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}
