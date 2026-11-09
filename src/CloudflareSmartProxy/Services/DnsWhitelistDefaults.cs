using System;
using System.Collections.Generic;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// DNS 加速白名单（<see cref="Models.AppSettings.GlobalDnsWhitelist"/>）的内置默认值：GitHub 一族域名。
///
/// 语义是「<b>默认条目始终保留</b>」：读配置和保存配置都会把它与用户填的内容合并去重，
/// 用户在「设置」页里删掉 github.com 也会被合回来。要整体停掉 DNS 加速，请关「启用 DNS 加速池解析 + 多 IP 竞速」
/// 总开关，或启动时取消「DNS优选」勾选——别指望把白名单清空。
///
/// 覆盖到的家族（`*.x` 匹配任意层子域，`x` 只匹配自身，两者都要写）：
/// github.com（站点本体、api、codeload、gist、raw 之外的多数入口）
/// githubusercontent.com（raw / camo / avatars / objects / media / private-user-images 等实际内容）
/// githubassets.com（页面静态资源）
/// github.io（GitHub Pages，正常由 CloudFront 承载、走 CDN 优选；解析结果不在网段里时这里兜底）
/// ghcr.io（容器镜像，同样是 CloudFront）
/// github.blog / githubstatus.com（博客与状态页）
/// githubcopilot.com（Copilot API）
/// </summary>
public static class DnsWhitelistDefaults
{
    /// <summary>内置默认白名单（新配置、以及「重置为默认」时显示的就是这一串）。</summary>
    public const string Text =
        "github.com, *.github.com, " +
        "githubusercontent.com, *.githubusercontent.com, " +
        "githubassets.com, *.githubassets.com, " +
        "github.io, *.github.io, " +
        "ghcr.io, *.ghcr.io, " +
        "github.blog, *.github.blog, " +
        "githubcopilot.com, *.githubcopilot.com, " +
        "githubstatus.com, *.githubstatus.com";

    /// <summary>
    /// 把用户填的条目并进内置默认：默认条目在前、去重、统一小写与尾点，返回可直接落盘/下发代理的整串文本。
    /// 条目之间的分隔符按 <see cref="DomainWildcardList"/> 那套（逗号/分号/顿号/空白/换行都算），所以多行粘贴进来也会被并平。
    /// </summary>
    public static string Merge(string? userText)
    {
        var entries = new List<string>(DomainWildcardList.NormalizeEntries(Text));
        var seen = new HashSet<string>(entries, StringComparer.Ordinal);
        foreach (var entry in DomainWildcardList.NormalizeEntries(userText))
            if (seen.Add(entry)) entries.Add(entry);
        return string.Join(", ", entries);
    }
}
