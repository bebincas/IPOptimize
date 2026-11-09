using CloudflareSmartProxy.Services;

namespace CloudflareSmartProxy.Models;

public sealed class AppSettings
{
    public int ProxyPort { get; set; } = 7890;

    /// <summary>「启动代理」旁的「CDN优选」勾选：true＝必须有 CDN 优选 IP 才允许启动；false＝本次运行不使用任何 CDN 优选池（CDN 域名走原始 DNS）。</summary>
    public bool CdnOptimizeEnabled { get; set; } = true;

    /// <summary>「启动代理」旁的「DNS优选」勾选：true＝必须有 DNS 加速池才允许启动（启用解密时还要根证书已被信任）；false＝本次运行不做 DNS 加速与解密。</summary>
    public bool DnsOptimizeEnabled { get; set; } = true;

    /// <summary>
    /// 点窗口「×」时的行为："Ask"＝每次都问（默认，还没记住选择）、"Tray"＝缩小到右下角托盘（代理继续跑）、"Exit"＝直接退出。
    /// 由关闭时那个询问框记住，也可以在托盘右键菜单的「关闭时缩小到托盘」勾选里改。
    /// </summary>
    public string CloseAction = "Ask";
    public int SamplesPerPrefix { get; set; } = 1;
    public int OptimizationCount { get; set; } = 10;
    public string SpeedTestUrl { get; set; } = "https://speed.cloudflare.com/__down?bytes=10000000";
    /// <summary>
    /// 优选结果列表（IPv4/IPv6 均可，附带 CDN 地区码）
    /// 使用 ObservableCollection 以便与 UI 直接绑定并在修改时自动更新
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<ProxyEndpoint> PreferredEdges { get; set; } = new();

    /*测速的Option选择参数*/
    /// <summary>线程并发数（默认 200，最多 1000）。</summary>
    public int Routines = 200;

    /// <summary>单个 IP 延迟测速次数（默认 4）。</summary>
    public int PingTimes = 4;

    /// <summary>单个 IP 下载测速最长时间（秒，默认 10）。</summary>
    public int DownloadTime = 10;

    /// <summary>测速端口（默认 443）。</summary>
    public int Port = 443;

    /// <summary>平均延迟下限（ms，默认 0）。</summary>
    public double MinDelayMs;

    /// <summary>平均延迟上限（ms，默认 9999）。</summary>
    public double MaxDelayMs = 9999;

    /// <summary>丢包率上限 0~1（默认 1.0）。</summary>
    public double MaxLossRate = 1.0;

    /// <summary>下载速度下限（MB/s，默认 0；&gt;0 时会测完全部候选直到凑够 TestCount）。</summary>
    public double MinSpeedMBs = 0;

    /// <summary>TCP 连接超时（ms，默认 1000）。</summary>
    public int ConnectTimeoutMs = 1000;

    /// <summary>下载测速并发数（Go 版串行，这里默认 4 个并行以提速）。</summary>
    public int DownloadConcurrency = 4;

    /// <summary>
    /// 情况2 GlobalDns 加速白名单（「设置」页维护）：逗号/分号/换行/空格分隔，支持 * 通配符，大小写不敏感。
    /// 只有命中的域名才会走「DNS 加速池并发解析 + 多 IP 竞速」加速。
    /// 默认值＝内置 GitHub 域名（<see cref="DnsWhitelistDefaults.Text"/>），并且读盘与保存都会把默认条目并回来，
    /// 所以这里删空也仍有 GitHub 加速；要整体停用请关 <see cref="DnsAccelEnabled"/> 或启动时的「DNS优选」勾选。
    /// </summary>
    public string GlobalDnsWhitelist { get; set; } = DnsWhitelistDefaults.Text;

    /*———————— DNS 加速 / 竞速 / MITM 参数（移植自 FastlyGithub）————————*/
    /// <summary>
    /// DNS 加速总开关：命中上面白名单的「非 CDN 域名」改用 DNS 加速池解析 + 多 IP 竞速，
    /// 而不是回退系统 DNS。加速池为空时本开关无效果（自动回退系统 DNS）。
    /// </summary>
    public bool DnsAccelEnabled = true;

    /// <summary>
    /// MITM 开关：白名单域名的 HTTPS 由代理用本地根 CA 签发的域名证书解密，再逐请求并发竞速取最快响应。
    /// 关闭时白名单域名仍享受 DNS 加速，但只做 TCP 竞速 + 透明隧道（不解密）。
    /// 开启需要在「设置」页把根证书装进当前用户的受信任根存储（Windows 会弹一次确认）。
    /// </summary>
    public bool MitmEnabled = true;

    /// <summary>DNS 扫描并发数（1～1024，默认 128）。</summary>
    public int DnsScanConcurrency = 128;

    /// <summary>单个 DNS 服务器查询超时（ms，默认 2000）。</summary>
    public int DnsScanTimeoutMs = 2000;

    /// <summary>竞速每批并发 IP 数（1～64，默认 8）；候选 IP 会分批全部试完，这不是总量上限。</summary>
    public int RaceMaxIps = 8;

    /// <summary>解析缓存 TTL 下限（秒，默认 60）。实际 TTL 取应答 A 记录的最小 TTL 并夹在上下限之间。</summary>
    public int DnsCacheMinTtlSec = 60;

    /// <summary>解析缓存 TTL 上限（秒，默认 600）。</summary>
    public int DnsCacheMaxTtlSec = 600;

    /// <summary>请求体缓冲上限（MB，默认 8）：超过则放弃逐请求竞速，退化为单连接流式转发。</summary>
    public int RaceBodyLimitMB = 8;

    /// <summary>竞速时单个候选 IP 的 TCP 建连上限（ms，默认 5000）。与测速用的 <see cref="ConnectTimeoutMs"/> 无关。</summary>
    public int RaceConnectTimeoutMs = 5000;

    /// <summary>一批并发竞速从建连到拿到响应头的总预算（ms，默认 25000）；每批各自享有。</summary>
    public int RaceBatchBudgetMs = 25000;

    /// <summary>MITM 逐请求竞速里单个候选的建连上限（ms，默认 6000）。</summary>
    public int MitmConnectTimeoutMs = 6000;

    /// <summary>需要 MITM 解密的 CONNECT 端口，逗号分隔（默认 "443"，可写 "443,8443"）。</summary>
    public string MitmPorts = "443";

    /// <summary>候选 IP 全部试完仍失败后的重赛轮数（0～5，默认 2）：治「刚才不行、过一会又行」，候选 IP 不会被拉黑，只是再并发试一遍。</summary>
    public int RaceRetryRounds = 2;

    /// <summary>每轮重赛前的等待（ms，100～60000，默认 800）。轮数 × 这个间隔＋每轮一批竞速预算＝最坏多等的时间。</summary>
    public int RaceRetryDelayMs = 800;

    /// <summary>「优选DNS」扫描后入选的 DNS 加速池（按延迟升序）。持久化以便重启后继续生效。</summary>
    public System.Collections.ObjectModel.ObservableCollection<DnsPoolEntry> DnsPool { get; set; } = new();

    /// <summary>「优选DNS」页最近一次扫描的域名（下次启动回填输入框）。</summary>
    public string DnsLastScanDomain { get; set; } = "github.com";

    /// <summary>加速池的来源域名（最近一次「应用到加速池」扫描的域名），仅用于界面展示。</summary>
    public string DnsPoolDomain { get; set; } = string.Empty;

    /// <summary>加速池最近一次更新时间；默认值＝从未应用过扫描结果。</summary>
    public DateTime DnsPoolUpdatedAt { get; set; }

    /// <summary>
    /// 浅拷贝一份用于单次测速运行：仅覆盖 SpeedTestUrl，其余选项与 PreferredEdges 引用保持不变。
    /// 给非 Cloudflare 档位测速时使用，避免把该档位的测速地址写进全局设置并被持久化
    /// （重启后全局 SpeedTestUrl 会回填给 Cloudflare 卡片）。
    /// </summary>
    public AppSettings CloneForRun(string speedTestUrl)
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.SpeedTestUrl = speedTestUrl;
        return copy;
    }
}
