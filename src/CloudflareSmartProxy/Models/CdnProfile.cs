using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CloudflareSmartProxy.Models;

/// <summary>
/// 界面上的一个 CDN 档位：勾选＝本次测速是否纳入；选中＝右侧专属参数显示谁。
/// 测速与代理分流按 Name 关联到 CdnRangeProviders 里的网段来源；勾选状态（Enabled）暂未参与逻辑。
/// </summary>
public sealed class CdnProfile : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>界面显示名，同时作为唯一标识写入 ProxyEndpoint.Cdn / IpResult.Cdn（旧数据默认 "Cloudflare"）。</summary>
    public string Name { get; init; } = string.Empty;

    private bool _enabled = true;
    /// <summary>是否纳入本次测速。</summary>
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; Raise(); } }
    }

    private string _speedTestUrl = string.Empty;
    /// <summary>该 CDN 的 HTTPS 下载测速地址。</summary>
    public string SpeedTestUrl
    {
        get => _speedTestUrl;
        set { if (_speedTestUrl != value) { _speedTestUrl = value; Raise(); } }
    }

    private string _rangeSource = string.Empty;
    /// <summary>官方 IP 网段来源地址。</summary>
    public string RangeSource
    {
        get => _rangeSource;
        set { if (_rangeSource != value) { _rangeSource = value; Raise(); } }
    }


    private string _statusKind = "idle";
    /// <summary>状态灯样式：idle / ok / warn / err / run，由界面模板映射成颜色。</summary>
    public string StatusKind
    {
        get => _statusKind;
        set { if (_statusKind != value) { _statusKind = value; Raise(); Raise(nameof(StatusText)); } }
    }

    private string _statusText = "未测速";
    /// <summary>状态行文字，例如「42 个 · 12 分钟前」。</summary>
    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; Raise(); } }
    }

    private int _edgeCount;
    /// <summary>该 CDN 在代理列表里的 IP 数（由界面按 PreferredEdges 聚合出来的展示值）。</summary>
    public int EdgeCount
    {
        get => _edgeCount;
        set { if (_edgeCount != value) { _edgeCount = value; Raise(); Raise(nameof(EdgeCountText)); } }
    }

    /// <summary>池为 0 时代理页标红，用文本让 XAML 不必再写触发器。</summary>
    public string EdgeCountText => Enabled ? EdgeCount.ToString() : "—";

    private DateTime? _lastRunLocal;
    /// <summary>上次测速完成时间（界面展示；逻辑暂未写入）。</summary>
    public DateTime? LastRunLocal
    {
        get => _lastRunLocal;
        set { if (_lastRunLocal != value) { _lastRunLocal = value; Raise(); Raise(nameof(LastRunText)); } }
    }

    public string LastRunText => LastRunLocal is null ? "从未" : LastRunLocal.Value.ToString("MM-dd HH:mm");

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
