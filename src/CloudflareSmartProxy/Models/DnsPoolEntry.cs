using System;
using System.Net;
using CloudflareSmartProxy.Services;

namespace CloudflareSmartProxy.Models;

/// <summary>
/// DNS 加速池里的一条记录：一个被扫描验证过、能解析出目标域名的公共 DNS 服务器及其响应延迟。
/// 池按 <see cref="LatencyMs"/> 升序排列，解析某个域名时整池一次性并发问完（见 <c>ResolverHub</c>）。
/// 只持久化这三项：重启后按同一排行继续解析，具体域名 IP 交给 <c>DnsResultCache</c> 现查现缓存。
/// </summary>
public sealed class DnsPoolEntry
{
    /// <summary>DNS 服务器 IPv4 地址（字符串形式，便于 JSON 与界面绑定）。</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>服务器归属/名称（来自内置列表的 hostname，可能为空）。</summary>
    public string? Name { get; set; }

    /// <summary>扫描时该 DNS 应答目标域名的延迟（ms），越小越靠前。</summary>
    public int LatencyMs { get; set; }

    /// <summary>界面展示用：名称为空时退回 IP 的字符串形式。</summary>
    public string Display => string.IsNullOrWhiteSpace(Name) ? Ip : $"{Name}";

    /// <summary>窄列紧凑列表（「开始优化」页的 DNS 加速池）用的一行提示：归属 + 延迟。</summary>
    public string RowTip => $"{Display}｜{LatencyMs}ms";

    public DnsSpeedEntry ToSpeedEntry() =>
        new(IPAddress.Parse(Ip), Name, LatencyMs);

    public static DnsPoolEntry From(DnsSpeedEntry e) =>
        new() { Ip = e.Server.ToString(), Name = e.Name, LatencyMs = e.LatencyMs };
}
