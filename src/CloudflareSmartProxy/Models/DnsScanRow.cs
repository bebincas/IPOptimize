namespace CloudflareSmartProxy.Models;

/// <summary>
/// 「优选DNS」扫描结果表的一行：一条 DNS 服务器解析出某个 IP 的记录。
/// 表格按 <see cref="Ip"/> 分组，组内按 <see cref="LatencyMs"/> 升序，组间按 <see cref="GroupRank"/> 升序。
/// </summary>
public sealed class DnsScanRow
{
    public required string Ip { get; init; }
    public required string DnsServer { get; init; }
    public required string DnsName { get; init; }
    public required int LatencyMs { get; init; }
    public required int GroupRank { get; init; }
    public int GroupCount { get; init; }
}
