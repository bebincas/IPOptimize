using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Services;

/// <summary>一个备选 DNS 服务器。</summary>
public sealed record DnsServerEntry(IPAddress Ip, string? Name)
{
    public override string ToString() => Name is null ? Ip.ToString() : $"{Ip} ({Name})";
}

/// <summary>
/// 一次 DNS 列表加载的结果：Servers 是合并完自定义文件的最终列表，
/// Origin 说明这批数据到底从哪来（中文，直接拼进界面状态与日志，免得猜这次用的是缓存还是内嵌）。
/// </summary>
public sealed record DnsServerListResult(IReadOnlyList<DnsServerEntry> Servers, string Origin)
{
    public int Count => Servers.Count;
}

/// <summary>
/// DNS 服务器列表加载：在线优先（<see cref="RemoteUrl"/>）→ 本地缓存（<see cref="CacheFileName"/>，
/// 7 天内算新鲜）→ 内嵌资源兜底（Resources/public-dns-servers.json），最后再合并 exe 同目录的
/// dns-servers.txt（用户自己追加的名单，任何时候都生效）。
/// 三个来源的 JSON 结构相同：只读根对象下的 servers 数组，条目形如 {"ip","hostname","countryName","isIpv6"...}，
/// 仅保留 IPv4、按 IP 去重；名称取 hostname，为空时退回 countryName。
/// 自定义文本格式：每行 "IP 名称(可选)"，# 开头为注释。
/// 取列表失败绝不让扫描开不了张：每一步失败只记 WARN 并退到下一个来源。
/// </summary>
public static class DnsServerList
{
    public const string CustomFileName = "dns-servers.txt";

    /// <summary>在线列表地址（发布方自己托管的那份 JSON，格式与内嵌资源一致）。</summary>
    public const string RemoteUrl = "http://gh-go.com/data/public-dns-servers.json";

    /// <summary>在线列表的本地缓存文件名，落在与 settings.json 同一个目录（<see cref="SettingsStore.DirectoryPath"/>）。</summary>
    public const string CacheFileName = "public-dns-servers.json";

    private const string EmbeddedResourceName = "CloudflareSmartProxy.Resources.public-dns-servers.json";

    /// <summary>缓存新鲜期：7 天内直接用本地文件、不联网；过期才重新下载。</summary>
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// 少于此条数就当这份 JSON 不可用（防的是「拿回来一个错误页 / 半截文件 / 上游改了结构」——
    /// 那种文件解析得出来但只有几条，扫描对着三五个 DNS 空跑一场，比直接失败更难查）。
    /// </summary>
    private const int MinUsableEntries = 50;

    /// <summary>整体预算 20 秒（HttpClient 内置超时；.NET 7+ 超时抛的是 TaskCanceledException，属于 OCE 家族）。</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static DnsServerList()
    {
        // 裸 HttpClient 没有 UA 容易被 CDN/防护侧直接 403（与顶部公告位同一套考虑）。
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("IPOptimize");
    }

    /// <summary>
    /// 取本次要用的 DNS 列表：缓存新鲜就用缓存（不联网），否则联网下载并回写缓存；
    /// 下载失败则退回已有缓存（哪怕过期），什么都没有才用内嵌资源。最后合并 dns-servers.txt。
    /// </summary>
    /// <param name="baseDirectory">自定义列表（dns-servers.txt）所在目录，默认 exe 同目录。</param>
    /// <param name="cancellationToken">手动「停止扫描」的令牌：下载列表阶段也要能被它打断。</param>
    /// <param name="remoteUrlOverride">临时换源（自检/调试用），为空则用 <see cref="RemoteUrl"/>。</param>
    public static async Task<DnsServerListResult> LoadAsync(
        string? baseDirectory = null,
        CancellationToken cancellationToken = default,
        string? remoteUrlOverride = null)
    {
        var remoteUrl = string.IsNullOrWhiteSpace(remoteUrlOverride) ? RemoteUrl : remoteUrlOverride.Trim();
        var cachePath = Path.Combine(SettingsStore.DirectoryPath, CacheFileName);
        var (cachedJson, cacheAge) = ReadCache(cachePath);
        var cachedList = cachedJson is null ? null : TryParse(cachedJson, "本地缓存 DNS 列表");

        IReadOnlyList<DnsServerEntry> baseList;
        string origin;

        if (cachedList is not null && cacheAge < CacheMaxAge)
        {
            baseList = cachedList;
            origin = $"本地缓存（{cacheAge.TotalDays:0.#} 天前）";
        }
        else
        {
            var remoteJson = await TryDownloadAsync(remoteUrl, cancellationToken);
            var remoteList = remoteJson is null ? null : TryParse(remoteJson, "在线 DNS 列表");
            if (remoteList is not null)
            {
                // 解析通过才落盘：坏数据不该覆盖上一次的好缓存。
                WriteCache(cachePath, remoteJson!);
                baseList = remoteList;
                origin = "在线获取";
            }
            else if (cachedList is not null)
            {
                baseList = cachedList;
                origin = $"本地缓存（已过期 {cacheAge.TotalDays:0.#} 天，联网取回失败）";
            }
            else
            {
                baseList = LoadDefault();
                origin = "内嵌列表（在线与缓存都不可用）";
            }
        }

        AppLog.Debug($"DNS 列表：{baseList.Count} 个，来源 {origin}");
        return new DnsServerListResult(MergeCustom(baseList, baseDirectory), origin);
    }

    /// <summary>加载内嵌默认列表（JSON 中 servers 键下的条目）。</summary>
    public static IReadOnlyList<DnsServerEntry> LoadDefault()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"内嵌资源 {EmbeddedResourceName} 缺失");

        using var doc = JsonDocument.Parse(stream);
        return ReadServers(doc.RootElement, "内嵌 DNS 列表");
    }

    /// <summary>解析一段 JSON 文本的 servers 数组（供外部/测试复用）。</summary>
    public static IReadOnlyList<DnsServerEntry> ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadServers(doc.RootElement, "DNS 列表 JSON");
    }

    /// <summary>读取根对象下 servers 键的数组，跳过非法/IPv6 条目并按 IP 去重。</summary>
    private static IReadOnlyList<DnsServerEntry> ReadServers(JsonElement root, string label)
    {
        if (!root.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"{label} 缺少 servers 数组");

        var list = new List<DnsServerEntry>();
        var seen = new HashSet<IPAddress>();
        foreach (var item in servers.EnumerateArray())
        {
            var entry = ParseJsonEntry(item);
            if (entry is not null && seen.Add(entry.Ip)) list.Add(entry);
        }
        return list;
    }

    /// <summary>解析并做数量合理性校验；非法或过少返回 null（内部已记 WARN）。</summary>
    private static IReadOnlyList<DnsServerEntry>? TryParse(string json, string label)
    {
        try
        {
            var list = ParseJson(json);
            if (list.Count < MinUsableEntries)
            {
                AppLog.Warn($"{label} 只有 {list.Count} 条可用 DNS（少于 {MinUsableEntries}），视为不可用");
                return null;
            }
            return list;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"解析{label}失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 拉在线列表。只有「调用方明确取消」（手动停止扫描）才继续上抛，
    /// 其余一律 WARN + 返回 null 交给上层退到缓存/内嵌——包括 HttpClient 超时抛的那个 TaskCanceledException。
    /// </summary>
    private static async Task<string?> TryDownloadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await Http.GetAsync(url, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn($"在线 DNS 列表 HTTP {(int)resp.StatusCode}: {url}");
                return null;
            }
            var text = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (text.Length < 1024)
            {
                AppLog.Warn($"在线 DNS 列表响应过小（{text.Length} 字节），视为不可用: {url}");
                return null;
            }
            return text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"获取在线 DNS 列表失败 {url}: {ex.Message}");
            return null;
        }
    }

    /// <summary>读缓存文件，返回「文本 + 距今多久」；不存在/读不动都返回空文本。</summary>
    private static (string? Json, TimeSpan Age) ReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return (null, TimeSpan.Zero);
            var text = File.ReadAllText(path);
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            return (text, age < TimeSpan.Zero ? TimeSpan.Zero : age); // 本机时钟被往回调过时别让缓存永远算新鲜
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取 DNS 列表缓存失败 {path}: {ex.Message}");
            return (null, TimeSpan.Zero);
        }
    }

    /// <summary>回写缓存（先写临时文件再替换，避免半个文件把上次的好数据换掉）。</summary>
    private static void WriteCache(string path, string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"写入 DNS 列表缓存失败 {path}: {ex.Message}");
        }
    }

    /// <summary>加载 exe 同目录的自定义列表（若存在），并与传入的列表合并去重。</summary>
    private static IReadOnlyList<DnsServerEntry> MergeCustom(IReadOnlyList<DnsServerEntry> baseList, string? baseDirectory)
    {
        var all = new List<DnsServerEntry>(baseList);
        var seen = new HashSet<IPAddress>(all.Select(e => e.Ip));

        var path = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, CustomFileName);
        if (File.Exists(path))
        {
            try
            {
                foreach (var e in ParseLines(File.ReadAllText(path)))
                    if (seen.Add(e.Ip)) all.Add(e);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"读取自定义 DNS 列表失败 {path}: {ex.Message}");
            }
        }
        return all;
    }

    /// <summary>导出当前列表为可编辑文本（供用户自定义扩展）。</summary>
    public static string ExportAsText(IEnumerable<DnsServerEntry> entries) =>
        string.Join(Environment.NewLine, entries.Select(e => e.Name is null ? e.Ip.ToString() : $"{e.Ip} {e.Name}"));

    /// <summary>把 servers 数组里的一条 JSON 记录转成条目；非法/IPv6 返回 null。</summary>
    private static DnsServerEntry? ParseJsonEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (item.TryGetProperty("isIpv6", out var isIpv6) && isIpv6.ValueKind == JsonValueKind.True) return null; // 仅 IPv4

        var ipText = GetString(item, "ip");
        if (ipText is null ||
            !IPAddress.TryParse(ipText, out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork) return null;

        return new DnsServerEntry(ip, GetString(item, "hostname") ?? GetString(item, "countryName"));
    }

    private static string? GetString(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static IReadOnlyList<DnsServerEntry> ParseLines(string content)
    {
        var list = new List<DnsServerEntry>();
        var seen = new HashSet<IPAddress>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (!IPAddress.TryParse(parts[0], out var ip)) continue;
            if (ip.AddressFamily != AddressFamily.InterNetwork) continue; // 仅 IPv4
            if (!seen.Add(ip)) continue;

            list.Add(new DnsServerEntry(ip, parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : null));
        }
        return list;
    }
}
