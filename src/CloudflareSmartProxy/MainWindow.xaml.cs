using CloudflareSmartProxy.Models;
using CloudflareSmartProxy.Services;
using CloudflareSmartProxy.Utilities;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Runtime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CloudflareSmartProxy;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly LogService _log = new();
    private readonly SettingsStore _settingsStore = new();
    /// <summary>各 CDN 档位（均为任播）的网段来源；创建顺序 = 代理归属判定优先级（Cloudflare 最先）。</summary>
    private readonly IReadOnlyList<CdnRangeProvider> _cdnProviders;
    private readonly EdgeOptimizerV1 _optimizer;
    private ProxyServer? _proxy;
    private CancellationTokenSource? _optimizationCts;
    /// <summary>代理启动/停止进行中：期间禁止再次点击 ProxyButton，防止 await 空窗期重复进入。</summary>
    private bool _proxyToggleBusy;
    /// <summary>右下角托盘图标（只在缩小到托盘期间可见）；Loaded 里建好，真正退出时销毁。</summary>
    private TrayIconService? _tray;
    /// <summary>从托盘「退出程序」进来时置真：跳过关闭询问，直接走真正的关闭流程。</summary>
    private bool _exitRequested;
    /// <summary>XAML 里给 ProxyGrid 声明的那份 GroupStyle；列表空时摘掉它（见 <see cref="EnsureProxyListGrouping"/>）。</summary>
    private GroupStyle? _proxyGroupStyle;
    /// <summary>当前已订阅 CollectionChanged 的那个代理列表实例（_settings 被整体替换后会换实例）。</summary>
    private ObservableCollection<ProxyEndpoint>? _groupedProxyList;

    private AppSettings _settings = new();

    public ObservableCollection<IpResult> TestResults { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<ProxyEndpoint> ProxyList => _settings.PreferredEdges;
    public ObservableCollection<string> Logs => _log.Items;


    /// <summary>
    /// 界面上的 CDN 档位：只保留边缘 IP 为任播、可用官方网段扫描命中就近节点的档位
    /// （Cloudflare 与 Amazon CloudFront）；Fastly / Azure(Microsoft) / Google CDN 非任播，
    /// 按 IP 段筛选无法加速就近接入，已整体移除（含各自的网段解析）。
    /// 「测速当前 CDN」按选中档位取对应网段来源（见 CdnRangeProviders，档位名大小写不敏感匹配）。
    /// 注意：只有 Cloudflare 档（CdnProfiles[0]）的测速地址持久化到全局 AppSettings.SpeedTestUrl，
    /// CloudFront 档的 SpeedTestUrl/RangeSource 只在内存里，重启后回到这里的默认值；
    /// 勾选状态（Enabled，预留给批量测速）暂未参与任何逻辑。
    /// </summary>
    public ObservableCollection<CdnProfile> CdnProfiles { get; } = new()
    {
        new CdnProfile
        {
            Name = "Cloudflare",
            Enabled = true,
            SpeedTestUrl = "https://speed.cloudflare.com/__down?bytes=10000000",
            RangeSource = "https://www.cloudflare.com/ips-v4",
        },
        new CdnProfile { 
            Name = "Amazon CloudFront", 
            Enabled = true, 
            SpeedTestUrl = "https://d1.awsstatic.com/Controller_to_Processor_SCCs.pdf", 
            RangeSource = "https://ip-ranges.amazonaws.com/ip-ranges.json" //解析只取 service=CLOUDFRONT 且 region=GLOBAL 的边缘段，见 AmazonCloudFrontRangeProvider
        },
    };

    private CdnProfile? _selectedCdn;
    /// <summary>右侧「当前 CDN」卡片绑定的档位。</summary>
    public CdnProfile? SelectedCdn
    {
        get => _selectedCdn;
        set
        {
            if (ReferenceEquals(_selectedCdn, value)) return;
            _selectedCdn = value;
            OnPropertyChanged(nameof(SelectedCdn));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 版本号，<b>唯一来源是 csproj 里的 &lt;Version&gt;</b>（它会派生出 InformationalVersion 与 FileVersion；
    /// &lt;AssemblyVersion&gt; 是单独一行写的，不必然跟着走）。优先取 InformationalVersion（允许 "1.1.0-preview1" 这种非数字段），
    /// SDK 可能给它追加 <c>+&lt;commit&gt;</c>，所以切掉 '+' 之后的部分；取不到才退回 AssemblyVersion 的前三段。
    /// 露出位置：窗口标题（构造函数追加 " v…"）、「设置 → 关于」的 <c>AboutVersionText</c>、以及 exe 右键属性。
    /// 别在 XAML 或代码里写死字面量，否则发版改一处漏一处。
    /// </summary>
    private static string AppVersion { get; } =
        (typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3)
         ?? "0.0.0").Split('+')[0].Trim();

    public MainWindow()
    {
        InitializeComponent();
        // 窗口标题 = XAML 里那句 + 版本号（Loaded 里建托盘时也用这个 Title，托盘提示与标题栏自然一致，且都在 NotifyIcon 的 63 字上限内）。
        Title = $"{Title} v{AppVersion}";
        AboutVersionText.Text = AppVersion;   // 「设置 → 关于」里那行，与标题栏同一个来源
        DataContext = this;
        // Ensure the DataGrid view is sorted by SpeedMBs descending and supports live sorting
        var view = CollectionViewSource.GetDefaultView(TestResults);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(nameof(IpResult.SpeedMBs), ListSortDirection.Descending));
        if (view is ICollectionViewLiveShaping live && live.CanChangeLiveSorting)
        {
            try
            {
                live.LiveSortingProperties.Add(nameof(IpResult.SpeedMBs));
                live.IsLiveSorting = true;
            }
            catch { /* ignore if live sorting not supported at runtime */ }
        }
        _cdnProviders = CdnRangeProviders.CreateAll(CdnProfiles, _log);
        _optimizer = new EdgeOptimizerV1(_log);
        InitDnsServices();   // DNS 加速池 / 扫描 / 证书相关服务与表格绑定（见 MainWindow.Dns.cs）
        _log.ItemAdded += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (LogList is not null && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
            if (ProxyLogList is not null && ProxyLogList.Items.Count > 0)
                ProxyLogList.ScrollIntoView(ProxyLogList.Items[^1]);
        });
        // 默认选中 Cloudflare（目前唯一有真实测速来源的档位），并把结果表筛选下拉填好。
        SelectedCdn = CdnProfiles[0];
        CdnFilterComboBox.ItemsSource = new[] { "全部" }.Concat(CdnProfiles.Select(p => p.Name)).ToArray();
        CdnFilterComboBox.SelectedIndex = 0;

        // 代理列表的 CDN 分组在 Loaded 里挂：_settings 会被磁盘配置整体替换，
        // 默认视图是按集合实例缓存的，在构造函数里对旧实例加分组会失效。
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        
    }

    // ====================================================================
    //      睡眠/唤醒：WM_POWERBROADCAST → 让代理主动重建监听 socket
    // ====================================================================

    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMRESUMESUSPEND = 0x0007;      // 从睡眠/待机正常唤醒
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;    // 自动唤醒（定时器/网络唤醒，可能还锁着屏）

    /// <summary>
    /// 唤醒后网卡与协议栈已经重置过：挂起的 accept 通常会带着异常回来（<c>AcceptLoopAsync</c> 里已按
    /// 「退避 + 重建」兜住），但协议栈偶尔只是让它干等，那样就什么异常都不会有——监听端口看着还开着，
    /// 内核照常收 backlog，浏览器连得上却永远等不到响应＝「每次睡起来代理就失效」。
    /// 这里在收到唤醒广播时直接触发一次重建，把恢复变成确定的，而不是等异常。
    /// 缩到托盘时 HWND 仍在，照样收得到广播；必须在 <see cref="OnSourceInitialized"/> 挂（那时才有句柄）。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle))?.AddHook(PowerBroadcastHook);
    }

    private IntPtr PowerBroadcastHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST)
        {
            var code = unchecked((uint)wParam.ToInt64());
            if (code == PBT_APMRESUMESUSPEND || code == PBT_APMRESUMEAUTOMATIC)
                _proxy?.NotifyNetworkResumed();   // 代理没跑时 _proxy 已是 null；跑着但端口已停也会自己再判一次
        }
        return IntPtr.Zero;   // 不接管：handled 保持 false，别的窗口过程照常处理
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsStore.LoadAsync();
        // notify that ProxyList (which returns _settings.PreferredEdges) may have changed
        OnPropertyChanged(nameof(ProxyList));
        EnsureProxyListGrouping();
        PortTextBox.Text = _settings.ProxyPort.ToString();
        SamplesTextBox.Text = _settings.SamplesPerPrefix.ToString();
        // 测速地址现在由「当前 CDN」卡片双向绑定，不能再直接给 TextBox.Text 赋值（那会覆盖掉绑定）。
        // 旧版持久化的单个 SpeedTestUrl 继续沿用为 Cloudflare 档位的地址，保持原行为不变。
        if (!string.IsNullOrWhiteSpace(_settings.SpeedTestUrl))
            CdnProfiles[0].SpeedTestUrl = _settings.SpeedTestUrl;
        OptimizationCountTextBox.Text = _settings.OptimizationCount.ToString();
        // fill advanced speed-test controls
        RoutinesTextBox.Text = _settings.Routines.ToString();
        PingTimesTextBox.Text = _settings.PingTimes.ToString();
        DownloadTimeTextBox.Text = _settings.DownloadTime.ToString();
        SpeedPortTextBox.Text = _settings.Port.ToString();
        MinDelayTextBox.Text = _settings.MinDelayMs.ToString();
        MaxDelayTextBox.Text = _settings.MaxDelayMs.ToString();
        MaxLossRateTextBox.Text = _settings.MaxLossRate.ToString();
        MinSpeedTextBox.Text = _settings.MinSpeedMBs.ToString();
        ConnectTimeoutTextBox.Text = _settings.ConnectTimeoutMs.ToString();
        DownloadConcurrencyTextBox.Text = _settings.DownloadConcurrency.ToString();
        CdnOptimizeCheckBox.IsChecked = _settings.CdnOptimizeEnabled;
        DnsOptimizeCheckBox.IsChecked = _settings.DnsOptimizeEnabled;
        GlobalDnsWhitelistTextBox.Text = _settings.GlobalDnsWhitelist ?? string.Empty;
        FillDnsOptions(_settings);   // DNS 加速池 / 竞速参数回填（MainWindow.Dns.cs）

        ApplySavedEdges();
        RefreshCdnPoolCounts();
        // 启动即后台预热全部 CDN 网段（各来源 12 小时内重复刷新会被自动跳过），失败只写日志不影响使用。
        _ = WarmUpRangeProvidersAsync();
        // 顶部公告位：开窗拉一次「图片 + 链接」（见 MainWindow.Promo.cs），拉不到就整块收起，不挡开窗。
        InitPromo();
        // 启用了 MITM 时，启动只检查根证书状态，不再自动安装：安装会弹 Windows 确认框，只应由用户主动触发。
        EnsureMitmReady();
        // 右下角托盘
        InitTray();
        // 底部状态栏先刷一遍（代理没跑也要显示「累计 0」和那句注意事项，而不是 XAML 里的占位文字）。
        RefreshStatsBar(true);

        _log.Info("程序已就绪。先执行优选，再启动代理。未优选时所有请求使用原始 DNS。 ");
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // 点「×」先问一次：缩小到右下角托盘，还是直接退出；记住之后不再问（想改就在托盘菜单里勾）。
        // 注意这段必须在任何 await 之前跑完：MainWindow_Closing 是 async void，WPF 不会等它，
        // 一跨过 await 就会当成「没取消」继续关窗。
        if (!_exitRequested)
        {
            var choice = AskCloseChoice();
            if (choice == CloseChoice.MinimizeToTray)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }
        }

        // 真正退出：托盘先摘掉，再按原流程停代理、关系统代理。
        _tray?.Dispose();
        _tray = null;

        _statsTimer?.Stop();
        _statsTimer = null;
        _optimizationCts?.Cancel();
        bool wasRunning = _proxy is not null;
        if (_proxy is not null)
        {
            await _proxy.StopAsync();
            _proxy = null;
        }
        // 代理没跑过就不去碰系统代理：关窗只负责关掉我们自己设的那一份。
        if (wasRunning)
            ClearSystemProxyQuietly();

    }

    private enum CloseChoice
    {
        MinimizeToTray,
        Exit,
        /// <summary>这次什么都不做（询问框点了「取消」）。</summary>
        KeepOpen,
    }

    /// <summary>
    /// 决定这次关窗该怎么办：已经记住过就直接照办；没记住就弹三选框（是＝缩小到托盘，否＝直接退出，取消＝这次不关）。
    /// 选了「是/否」会当场同步写回 settings.json——异步写可能来不及落盘（进程马上就要退了）。
    /// </summary>
    private CloseChoice AskCloseChoice()
    {
        if (string.Equals(_settings.CloseAction, "Tray", StringComparison.OrdinalIgnoreCase))
            return CloseChoice.MinimizeToTray;
        if (string.Equals(_settings.CloseAction, "Exit", StringComparison.OrdinalIgnoreCase))
            return CloseChoice.Exit;

        var answer = MessageBox.Show(
            "关闭这个窗口时，想怎么处理？\n\n" +
            "　是＝缩小到右下角托盘。\n" +
            "　否＝直接退出。\n" +
            "　取消＝这次先不关。\n\n" +
            "选完就会记住，以后不再问；想改的话在托盘图标右键菜单里勾/取消「关闭时缩小到托盘」。",
            "关闭窗口", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (answer is MessageBoxResult.Cancel or MessageBoxResult.None)
            return CloseChoice.KeepOpen;

        bool toTray = answer == MessageBoxResult.Yes;
        _settings.CloseAction = toTray ? "Tray" : "Exit";
        SaveCloseAction();
        return toTray ? CloseChoice.MinimizeToTray : CloseChoice.Exit;
    }

    /// <summary>收进托盘：只藏窗口，代理与系统代理一律不动——这正是「缩小到托盘」的意义。</summary>
    private void MinimizeToTray()
    {
        if (_tray is null)
            return;   // 理论上到不了（Loaded 里就建好了）；真没建成就当这次关窗被取消，至少不会把代理留在后台无人管

        UpdateTrayState();
        _tray.Show();
        Hide();
        _tray.Balloon("已缩小到托盘，优选加速仍在运行。点图标恢复窗口，右键选「退出程序」才会真正退出。");
    }

    /// <summary>把窗口带回前台。除了托盘图标，也被单实例的「后来者敲门」复用（见 <see cref="App"/>）。</summary>
    internal void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        // 从托盘恢复时窗口经常压在别的窗口下面，Topmost 抖一下把它提到最前。
        Topmost = true;
        Topmost = false;
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    /// <summary>托盘菜单里改「关闭时缩小到托盘」＝改那个记住的选择，立刻落盘。</summary>
    private void OnTrayCloseToTrayChanged(bool toTray)
    {
        _settings.CloseAction = toTray ? "Tray" : "Exit";
        SaveCloseAction();
        _log.Info($"以后点窗口关闭按钮将{(toTray ? "缩小到右下角托盘" : "直接退出")}（可在托盘菜单里改）。");
    }

    private void SaveCloseAction()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _log.Warn($"保存「关闭时怎么处理」失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 建托盘图标（在 Loaded 里调，此时 _settings 已从磁盘读好）。图标先不显示，
    /// 只有真的「缩小到托盘」才亮出来，平时不占托盘位置。
    /// </summary>
    private void InitTray()
    {
        // 标题栏那句（含构造函数里追加的版本号）直接复用，别再抄一份字面量。
        _tray = new TrayIconService(Title);
        _tray.ShowRequested += ShowFromTray;
        _tray.ExitRequested += ExitFromTray;
        _tray.CloseToTrayChanged += OnTrayCloseToTrayChanged;
        _tray.SetCloseToTray(string.Equals(_settings.CloseAction, "Tray", StringComparison.OrdinalIgnoreCase));
        UpdateTrayState();
        _tray.Show();
    }

    /// <summary>托盘里那行「当前优选」跟着代理实际状态走；代理启动/停止后都要再调一次。</summary>
    private void UpdateTrayState()
        => _tray?.SetRunning(_proxy is not null,
                             _proxy is null ? null : $"127.0.0.1:{_settings.ProxyPort}");

    /// <summary>
    /// 关闭本程序写入的系统代理并把结果写进日志。异常不外抛——关窗时不能因为注册表写入失败而卡住或崩溃。
    /// 操作是幂等的：已经关过（或代理被用户手动改走）时第二次调用不会改动注册表。
    /// </summary>
    private void ClearSystemProxyQuietly()
    {
        try
        {
            if (SystemProxy.ClearProxy(out string message))
                _log.Info(message);
            else
                _log.Debug(message);
        }
        catch (Exception ex)
        {
            _log.Warn($"关闭系统代理失败：{ex.Message}");
        }
    }

    private bool TryReadSettings(out AppSettings settings)
    {
        settings = _settings;
        if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1024 or > 65535)
        {
            MessageBox.Show("代理端口必须是 1024～65535。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!int.TryParse(SamplesTextBox.Text, out var samples) || samples is < 1 or > 64)
        {
            MessageBox.Show("每网段采样数必须是 1～64。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!int.TryParse(OptimizationCountTextBox.Text, out var optimizationCount) || optimizationCount is < 10 or > 30)
        {
            MessageBox.Show("优选代理数量必须是 10～30。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // 「当前 CDN」卡片绑定的是 SelectedCdn：只有 Cloudflare 档（CdnProfiles[0]）的地址才写入全局 SpeedTestUrl；
        // 其余档位的测速地址仅留在档位对象上（内存值，不持久化），由「测速当前 CDN」入口单独校验，
        // 避免用其它档位的地址覆盖掉重启后回填给 Cloudflare 卡片的默认值。
        string globalSpeedUrl;
        if (SelectedCdn is null || ReferenceEquals(SelectedCdn, CdnProfiles[0]))
        {
            if (!Uri.TryCreate(SpeedUrlTextBox.Text.Trim(), UriKind.Absolute, out var speedUri) ||
                speedUri.Scheme != Uri.UriSchemeHttps)
            {
                MessageBox.Show("测速地址必须是有效的 HTTPS URL。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            globalSpeedUrl = speedUri.ToString();
        }
        else
        {
            globalSpeedUrl = _settings.SpeedTestUrl;
        }

        // start from existing settings then override from UI where valid
        var newSettings = new AppSettings
        {
            ProxyPort = port,
            SamplesPerPrefix = samples,
            OptimizationCount = optimizationCount,
            SpeedTestUrl = globalSpeedUrl,
            PreferredEdges = _settings.PreferredEdges, // retain existing preferred edges
            DnsPool = _settings.DnsPool,               // 加速池由「优选DNS」页维护，这里只保留引用
            DnsPoolDomain = _settings.DnsPoolDomain,
            DnsPoolUpdatedAt = _settings.DnsPoolUpdatedAt,
        };

        // 以下整数项会直接进入网络/并发逻辑，越界会导致卡死或抛异常，必须强制校验范围。
        if (!TryReadIntOption(RoutinesTextBox.Text, "线程并发数", 1, 1000, _settings.Routines, out newSettings.Routines))
            return false;
        if (!TryReadIntOption(PingTimesTextBox.Text, "Ping 次数", 1, int.MaxValue, _settings.PingTimes, out newSettings.PingTimes))
            return false;
        if (!TryReadIntOption(DownloadTimeTextBox.Text, "下载最长(秒)", 1, 600, _settings.DownloadTime, out newSettings.DownloadTime))
            return false;
        if (!TryReadIntOption(SpeedPortTextBox.Text, "测速端口", 1, 65535, _settings.Port, out newSettings.Port))
            return false;

        if (double.TryParse(MinDelayTextBox.Text, out var minDelay)) newSettings.MinDelayMs = minDelay;
        else newSettings.MinDelayMs = _settings.MinDelayMs;
        if (double.TryParse(MaxDelayTextBox.Text, out var maxDelay)) newSettings.MaxDelayMs = maxDelay;
        else newSettings.MaxDelayMs = _settings.MaxDelayMs;

        if (double.TryParse(MaxLossRateTextBox.Text, out var maxLoss)) newSettings.MaxLossRate = maxLoss;
        else newSettings.MaxLossRate = _settings.MaxLossRate;

        if (double.TryParse(MinSpeedTextBox.Text, out var minSpeed)) newSettings.MinSpeedMBs = minSpeed;
        else newSettings.MinSpeedMBs = _settings.MinSpeedMBs;

        if (!TryReadIntOption(ConnectTimeoutTextBox.Text, "连接超时(ms)", 100, 60000, _settings.ConnectTimeoutMs, out newSettings.ConnectTimeoutMs))
            return false;
        if (!TryReadIntOption(DownloadConcurrencyTextBox.Text, "下载并发数", 1, 64, _settings.DownloadConcurrency, out newSettings.DownloadConcurrency))
            return false;
        // 「启动代理」旁的两个勾选也是设置的一部分（先取消勾选再点启动＝本次运行不做该种加速）
        newSettings.CdnOptimizeEnabled = CdnOptimizeCheckBox.IsChecked ?? _settings.CdnOptimizeEnabled;
        newSettings.DnsOptimizeEnabled = DnsOptimizeCheckBox.IsChecked ?? _settings.DnsOptimizeEnabled;
        // 白名单：内置 GitHub 域名始终保留，用户填的内容并进去（删掉默认条目也会被合回来）
        newSettings.GlobalDnsWhitelist = DnsWhitelistDefaults.Merge(GlobalDnsWhitelistTextBox.Text);
        if (!TryReadDnsOptions(newSettings))   // DNS 加速池 / 竞速 / MITM 参数（MainWindow.Dns.cs）
            return false;

        settings = newSettings;
        return true;
    }

    /// <summary>
    /// 解析整型测速选项：文本为空/非法时沿用旧值（返回 true）；能解析但超出 [min,max] 时弹出提示并返回 false 以中止保存。
    /// 用于堵住 Routines/DownloadConcurrency=0 触发 ParallelOptions 抛异常、以及 DownloadTime=0 使下载测速窗口无意义等问题。
    /// </summary>
    private bool TryReadIntOption(string text, string name, int min, int max, int previous, out int value)
    {
        if (!int.TryParse(text, out value))
        {
            value = previous;
            return true;
        }
        if (value < min || value > max)
        {
            MessageBox.Show($"{name} 必须在 {min}～{max} 之间。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private async void SaveSpeedOptions_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadSettings(out var settings))
            return;
        _settings = settings;
        await _settingsStore.SaveAsync(_settings);
        // 「设置」页的 GlobalDns 白名单同样走 TryReadSettings，保存后热更新运行中的代理
        _proxy?.UpdateGlobalDnsWhitelist(_settings.GlobalDnsWhitelist);
        ApplyDnsSettingsToProxy();   // DNS 加速池与竞速参数一并热更新
        _log.Info("测速选项已保存。");
        Toast("测速选项已保存。", ToastNotifier.Kind.Success);
    }

    /// <summary>「设置」页保存：与其他共享设置统一走 TryReadSettings 校验，落盘并热更新运行中代理的 GlobalDns 白名单。</summary>
    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadSettings(out var settings))
            return;
        _settings = settings;
        await _settingsStore.SaveAsync(_settings);
        _proxy?.UpdateGlobalDnsWhitelist(_settings.GlobalDnsWhitelist);
        ApplyDnsSettingsToProxy();   // DNS 加速 / MITM 参数热更新（含刚改的开关）
        // 这里是用户刚动过 MITM 勾选并点了「保存设置」，仍按老行为把证书装好；程序启动时不再自动安装。
        if (_settings.MitmEnabled && !EnsureMitmReady())
            InstallMitmCert();
        _log.Info("设置已保存。");
        // 域名白名单保存结果用 Toast 提示。内置 GitHub 域名始终保留（见 DnsWhitelistDefaults），
        // 所以正常不可能为 0；那条 warn 分支只是防止合并逻辑被改坏后静默「白名单为空＝不加速」。
        int count = DomainWildcardList.Parse(_settings.GlobalDnsWhitelist).Count;
        if (count == 0)
            Toast("域名白名单已保存（当前为空，全局解析加速未启用）。", ToastNotifier.Kind.Warn);
        else
            Toast($"域名白名单已保存（共 {count} 条，含内置 GitHub 域名），已同步到运行中的代理。", ToastNotifier.Kind.Success);
    }

    private void ResetSpeedOptions_Click(object sender, RoutedEventArgs e)
    {
        var def = new AppSettings();
        RoutinesTextBox.Text = def.Routines.ToString();
        PingTimesTextBox.Text = def.PingTimes.ToString();
        DownloadTimeTextBox.Text = def.DownloadTime.ToString();
        SpeedPortTextBox.Text = def.Port.ToString();
        MinDelayTextBox.Text = def.MinDelayMs.ToString();
        MaxDelayTextBox.Text = def.MaxDelayMs.ToString();
        MaxLossRateTextBox.Text = def.MaxLossRate.ToString();
        MinSpeedTextBox.Text = def.MinSpeedMBs.ToString();
        OptimizationCountTextBox.Text = def.OptimizationCount.ToString();
        ConnectTimeoutTextBox.Text = def.ConnectTimeoutMs.ToString();
        DownloadConcurrencyTextBox.Text = def.DownloadConcurrency.ToString();
        CdnOptimizeCheckBox.IsChecked = def.CdnOptimizeEnabled;
        DnsOptimizeCheckBox.IsChecked = def.DnsOptimizeEnabled;
        GlobalDnsWhitelistTextBox.Text = def.GlobalDnsWhitelist;
        ResetDnsOptions(def);   // DNS 加速 / 竞速 / MITM 参数一并重置（MainWindow.Dns.cs）
        _log.Info("测速选项已重置为默认值。");
    }

    /// <summary>
    /// 「关于」里的链接（官网 / GitHub 源码）：URL 就写在 XAML 的 NavigateUri 上，这里只负责打开。
    /// 必须走 <see cref="BrowserLauncher"/>，别改成 <c>Process.Start(url, UseShellExecute=true)</c>——
    /// 那是让 Windows 去判默认浏览器，某些机器上会退回 iexplore（与顶部公告位同一个坑）。
    /// BrowserLauncher 自己不抛异常，所以这里不包 try/catch。
    /// </summary>
    private void AboutLink_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;   // 拦住冒泡，别让带 NavigateUri 的 Hyperlink 走到 WPF 自己的导航上去（那才是交给 shell 判默认浏览器）
        if (sender is Hyperlink { NavigateUri: not null } link)
            BrowserLauncher.Open(link.NavigateUri.ToString());
    }

    private async void OptimizeButton_Click(object sender, RoutedEventArgs e)
    {
        //for test
        //test
        /*CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(10));
        var result = await _optimizer.DownloadOneAsync("172.64.150.103", new AppSettings() { SpeedTestUrl = "https://speed.cloudflare.com/__down?bytes=10000000", DownloadTime = 100 }, cancellationTokenSource.Token);
        var aa = result.SpeedBps / (1024.0 * 1024.0);
        return;*/



        if (_optimizationCts is not null)
        {
            _optimizationCts.Cancel();
            return;
        }

        if (!TryReadSettings(out var settings))
            return;

        // 接入所选 CDN：本次测速用该档位对应的网段来源；非 Cloudflare 档的测速地址不走全局校验，这里单独检查。
        var runCdn = SelectedCdn;
        var runProvider = runCdn is null ? null : FindProviderFor(runCdn);
        if (runCdn is null || runProvider is null)
        {
            MessageBox.Show("当前选中的 CDN 档位尚未接入网段来源，无法测速。", "无法测速", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        runProvider.RangeSourceUrl = runCdn.RangeSource;
        AppSettings runSettings = settings;
        if (!ReferenceEquals(runCdn, CdnProfiles[0]))
        {
            if (!Uri.TryCreate((runCdn.SpeedTestUrl ?? string.Empty).Trim(), UriKind.Absolute, out var runUri) ||
                runUri.Scheme != Uri.UriSchemeHttps)
            {
                MessageBox.Show($"「{runCdn.Name}」的测速地址无效：请在「当前 CDN」卡片填写有效的 HTTPS URL。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // 克隆本次运行参数：非 Cloudflare 档位的测速地址不写回全局设置，
            // 否则会被持久化并在重启后回填给 Cloudflare 卡片。
            runSettings = settings.CloneForRun(runUri.ToString());
        }

        _settings = settings;
        TestResults.Clear();
        _optimizationCts = new CancellationTokenSource();
        ProxyButton.IsEnabled = false;
        SetTestingUi(true, $"测速中（当前 CDN：{runCdn?.Name ?? "-"}）");
        if (runCdn is not null)
        {
            runCdn.StatusKind = "run";
            runCdn.StatusText = "测速中…";
        }

        try
        {
            var progress = new Progress<IpResult>(result =>
            {
                // 结果归属当前 CDN，供结果表的 CDN 列与筛选使用。
                if (string.IsNullOrEmpty(result.Cdn) || result.Cdn == "Cloudflare")
                    result.Cdn = runCdn?.Name ?? result.Cdn;

                var old = TestResults.FirstOrDefault(x => x.Ip == result.Ip);
                if (old is not null)
                    TestResults.Remove(old);
                TestResults.Add(result);
            });

            var selection = await _optimizer.OptimizeAsync(runProvider, runSettings, progress, _optimizationCts.Token);

            // 优选完成后询问是否用本次结果覆盖当前 CDN 的条目。
            // 注意：只替换该 CDN 自己的池，其它 CDN 已优选出来的条目必须保留。
            bool overwrite;
            if (selection.Count == 0)
            {
                _log.Warn("本次优选未获得可用 IP。");
                overwrite = false;
            }
            else
            {
                var confirm = MessageBox.Show(
                    $"「{runCdn?.Name}」优选完成，本次共 {selection.Count} 个快速 IP。\n\n" +
                    $"是否用本次结果覆盖代理列表中 {runCdn?.Name} 的条目？（其它 CDN 的列表不受影响）",
                    "覆盖确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes);
                overwrite = confirm == MessageBoxResult.Yes;
            }

            if (overwrite)
                OverwriteCdnPool(runCdn!, selection); // runCdn 在方法入口已判空，这里只是绕开 lambda 捕获导致的流分析放宽
            else
                _log.Info("已取消覆盖，保留当前代理列表。");

            if (runCdn is not null)
            {
                runCdn.LastRunLocal = DateTime.Now;
                runCdn.StatusKind = selection.Count > 0 ? "ok" : "err";
            }
            RefreshCdnPoolCounts();
            await _settingsStore.SaveAsync(_settings);
            ApplySavedEdges();
        }
        catch (OperationCanceledException)
        {
            _log.Warn("优选已停止。");
            if (runCdn is not null) runCdn.StatusKind = runCdn.EdgeCount > 0 ? "ok" : "idle";
            //todo 如果用户暂停了，那就从已经有的TestResults中选出最优的几个IP作为Primary/Backup，保存到Settings里
        }
        catch (Exception ex)
        {
            _log.Error($"优选失败：{ex.Message}");
            if (runCdn is not null) { runCdn.StatusKind = "err"; runCdn.StatusText = "测速失败"; }
            MessageBox.Show(ex.Message, "优选失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _optimizationCts.Dispose();
            _optimizationCts = null;
            ProxyButton.IsEnabled = !_proxyToggleBusy; // 启动/停止尚未结束时保持禁用，防重复点击
            SetTestingUi(false, "空闲");
        }
    }

    private async void ProxyButton_Click(object sender, RoutedEventArgs e)
    {
        // 启动/停止内部有多个 await（网段刷新、绑端口、写系统代理等），期间 _proxy 尚未赋值 / 还没清空，
        // 连点会重复起一个代理或重复读写系统代理；先用重入标志拦截，同时禁用按钮给出视觉反馈。
        if (_proxyToggleBusy)
        {
            Toast("代理正在启动/停止，请稍候…", ToastNotifier.Kind.Warn, 1200);
            return;
        }
        _proxyToggleBusy = true;
        ProxyButton.IsEnabled = false;
        try
        {
            if (_proxy is not null)
            {
                await _proxy.StopAsync();
                _proxy = null;
                ProxyButton.Content = "启动优化";
                StatusText.Text = "优化未启动";
                StatusDot.Fill = Brushes.Gray;
                PortTextBox.IsEnabled = true;
                StopStatsBar();   // 停表并把最终数字留在状态栏上
                ClearSystemProxyQuietly();
                UpdateTrayState();
                Toast("优化已停止，系统代理已关闭。");
                return;
            }

            if (!TryReadSettings(out var settings))
                return;

            _settings = settings;
            await _settingsStore.SaveAsync(_settings);
            try
            {
                // 「CDN优选 / DNS优选」声明的是本次运行要用哪些加速方式：勾了就必须有对应成果，
                // 否则宁可不启动——免得用户以为在加速，实际全程走原始 DNS。
                bool useCdn = _settings.CdnOptimizeEnabled;
                bool useDns = _settings.DnsOptimizeEnabled;

                if (useCdn && _settings.PreferredEdges.Count == 0)
                {
                    MessageBox.Show(
                        "已勾选「CDN优选」，但优选 IP 列表为空。\n\n请先在对应 CDN 档位执行优选，或取消该勾选后再启动。",
                        "无法启动代理", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (useDns && _settings.DnsPool.Count == 0)
                {
                    MessageBox.Show(
                        "已勾选「DNS优选」，但 DNS 加速池为空。\n\n请到「优选DNS」页扫描，或取消该勾选后再启动。",
                        "无法启动代理", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 解密（MITM）要求根证书已被系统信任：没装就当场问一次要不要装；
                // 用户拒绝或安装失败 → 本次运行不解密，代理照常启动（证书随时可在「设置」页补装）。
                bool mitmForRun = useDns && _settings.MitmEnabled;
                if (mitmForRun && !EnsureMitmReady())
                {
                    var answer = MessageBox.Show(
                        "已勾选「DNS优选」，但本地根证书还没有安装到系统。\n\n" +
                        "选「是」：现在安装证书。\n\n" +
                        "选「否」：代理照常启动，但会影响白名单域名的加速效果（如：github优选加速效果不佳）。",
                        "需要安装根证书", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    mitmForRun = answer == MessageBoxResult.Yes && InstallMitmCert();
                    if (answer == MessageBoxResult.Yes && !mitmForRun)
                        MessageBox.Show("根证书安装未完成（可能取消了 Windows 的确认框）。本次运行不解密 HTTPS。",
                            "未安装", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                Toast($"正在启动代理 127.0.0.1:{_settings.ProxyPort} …", ToastNotifier.Kind.Info, 1600);
                SyncRangeSources();
                // 启动前刷新全部 CDN 网段（Loaded 已预热的档位，12 小时内这里是空操作）。
                await Task.WhenAll(_cdnProviders.Select(p => p.RefreshAsync(CancellationToken.None)));
                // 只要本次可能用 DNS 优选就把根 CA 备好：MITM 开关可以在代理运行期间于「设置」页随时勾选，不必重启代理。
                // 取消「CDN优选」＝本次运行下发空池，CDN 域名一律走原始 DNS。
                IEnumerable<ProxyEndpoint> edgesForRun = useCdn ? _settings.PreferredEdges : Array.Empty<ProxyEndpoint>();
                _proxy = new ProxyServer(_cdnProviders, _log, edgesForRun,
                    _dnsResolver, _dnsResolver.Stats, useDns ? EnsureCa() : null, _proxyOptions);
                // 情况2 的 GlobalDns 加速白名单随代理实例下发（保存「设置」时可热更新）
                _proxy.UpdateGlobalDnsWhitelist(_settings.GlobalDnsWhitelist);
                // 按本次运行的勾选下发 DNS 加速 / MITM（不改 settings.json 里的持久化值）
                ApplyDnsSettingsToProxy(useDns && _settings.DnsAccelEnabled, mitmForRun);
                _log.Info($"本次运行的加速方式：CDN优选={(useCdn ? "开" : "关")}" +
                          $"（{(useCdn ? _settings.PreferredEdges.Count : 0)} 个地址）｜" +
                          $"DNS优选={(useDns ? "开" : "关")}（{(useDns ? _settings.DnsPool.Count : 0)} 个 DNS）｜" +
                          $"HTTPS解密={(_proxy.IsMitmActive ? "开" : "关")}。");
                if (_proxy.IsMitmActive)
                    _log.Warn("DNS 加速解密（MITM）已启用：白名单域名的 HTTPS 会被本地根证书解密后逐请求竞速。");
                await _proxy.StartAsync(_settings.ProxyPort);
                try
                {
                    SystemProxy.SetProxy($"127.0.0.1:{_settings.ProxyPort}");
                    _log.Info($"系统代理已指向 127.0.0.1:{_settings.ProxyPort}，停止或退出时自动关闭。");
                }
                catch (Exception ex)
                {
                    _log.Warn($"设置系统代理失败：{ex.Message}。代理仍在运行，可在浏览器里手动指向 127.0.0.1:{_settings.ProxyPort}。");
                }
                ProxyButton.Content = "停止优化";
                StatusText.Text = $"运行中 · 127.0.0.1:{_settings.ProxyPort}";
                StatusDot.Fill = Brushes.Green;
                PortTextBox.IsEnabled = false;
                StartStatsBar();   // 底部状态栏开始每秒刷运行统计
                UpdateTrayState();
                Toast($"代理已启动 · 127.0.0.1:{_settings.ProxyPort}", ToastNotifier.Kind.Success);
            }
            catch (Exception ex)
            {
                _proxy = null;
                _log.Error($"代理启动失败：{ex.Message}");
                MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _proxyToggleBusy = false;
            // 测速流程会临时禁用代理按钮，这里只在没有测速进行时才恢复可点。
            ProxyButton.IsEnabled = _optimizationCts is null;
        }
    }

    // ====================================================================
    //          底部状态栏：代理运行统计（替代高频的逐连接日志）
    // ====================================================================

    /// <summary>
    /// 「每次连接/每个请求成功」这类日志已从界面降到 DEBUG（只写 %LOCALAPPDATA%\CloudflareSmartProxy\logs），
    /// 加速是否在生效改由底部那一行计数表达。统计实例是 <see cref="ResolverHub.Stats"/>（由本窗口持有并传进 ProxyServer），
    /// 代理停止/重启都不清零，所以文案统一标成累计。
    /// </summary>
    private DispatcherTimer? _statsTimer;

    /// <summary>ToolTip 里的每域名明细走 TopWinners（锁内只取前 10，不再整体拷贝 Winners 字典），仍保持 5 秒刷一次。</summary>
    private int _statsTick;

    private void StartStatsBar()
    {
        if (_statsTimer is not null)
            return;
        _statsTick = 0;
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => RefreshStatsBar();
        _statsTimer.Start();
    }

    private void StopStatsBar()
    {
        if (_statsTimer is null)
            return;
        _statsTimer.Stop();
        _statsTimer = null;
        RefreshStatsBar(true);   // 停止后再刷一遍，把最终数字留在状态栏上
    }

    /// <param name="forceTip">true＝连 ToolTip 明细一起重刷（代理刚启动/刚停止、开窗初始化时用）。</param>
    private void RefreshStatsBar(bool forceTip = false)
    {
        var s = _dnsResolver.Stats;
        var last = s.LastWinner;
        StatsBar.Text =
            $"运行统计（累计）：CDN 优选 {s.EdgeConnections} 条 · 隧道 {s.Tunnels} · MITM 会话 {s.MitmConnects} · " +
            $"DNS 竞速 {s.RaceRequests} 次 · 解析缓存命中 {s.CacheHits} · 失败 {s.Errors}" +
            (string.IsNullOrEmpty(last) ? "" : $" · 最近胜出 {last}");

        if (!forceTip && _statsTick++ % 5 != 0)
            return;

        var winners = s.TopWinners(10)
            .Select(w => $"　{w.Host} → {w.Ip} ×{w.Count}")
            .ToArray();

        var tip = new List<string>
        {
            "CDN 优选＝走优选边缘 IP 建连成功的连接（情况1，含缓存命中）；隧道＝未解密的 CONNECT；",
            "MITM 会话＝解密后的 CONNECT；解密请求＝MITM 里逐请求竞速的 HTTP 请求；",
            "DNS 竞速＝竞速批次（含全失败后的重赛轮）；解析缓存命中＝该域名的条目里现成就有 IP，无需重新全池扫描；",
            "失败＝候选 IP 全部连不上（界面日志里仍会留下一条 WARN）。",
            "「哪条连接走了哪个 IP」的逐条明细只写文件：logs\\proxy-yyyyMMdd.log。",
            "注意：代理仅监听 127.0.0.1，请勿将该端口开放到局域网或公网；优选失败自动回退域名原始 DNS。",
        };
        if (winners.Length > 0)
        {
            tip.Add("");
            tip.Add("各域名胜出 IP（按次数取前 10）：");
            tip.AddRange(winners);
        }
        StatsBar.ToolTip = string.Join(Environment.NewLine, tip);
    }

    private void ApplySavedEdges()
    {
        // 主/备用 IP 显示已移至界面简化，保留设置值以便代理使用
    }

    private async void AddToProxy_Click(object sender, RoutedEventArgs e)
    {
        var selected = TestResults.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先勾选要添加的 IP。", "未选择", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var changed = false;
        foreach (var ip in selected)
        {
            var ipStr = ip.Ip?.Trim();
            if (string.IsNullOrEmpty(ipStr))
            {
                ip.Selected = false;
                continue;
            }

            // avoid duplicates (case-insensitive) when adding
            if (!_settings.PreferredEdges.Any(x => string.Equals(x.Ip, ipStr, StringComparison.OrdinalIgnoreCase)))
            {
                _settings.PreferredEdges.Add(new ProxyEndpoint { Ip = ipStr, Colo = ip.Colo, Cdn = ip.Cdn });
                changed = true;
            }
            // clear checkbox after adding
            ip.Selected = false;
        }

        if (!changed)
            return;

        await CommitProxyListChangeAsync();
        _log.Info("已更新代理优选列表。");
    }

    /// <summary>清空整个代理列表（带二次确认）。</summary>
    private async void ClearProxyList_Click(object sender, RoutedEventArgs e)
    {

        var confirm = MessageBox.Show(
            $"确定清空代理列表（共 {_settings.PreferredEdges.Count} 个 IP）吗？此操作不可撤销。",
            "清空确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (confirm != MessageBoxResult.Yes)
            return;

        _settings.PreferredEdges.Clear();
        await CommitProxyListChangeAsync();
        _log.Info("已清空代理列表。");
    }

    /// <summary>删除单条代理 IP（列表每行“删除”文字，DataContext 即该行 ProxyEndpoint）。</summary>
    private async void DeleteProxyRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement rowButton)
            return;
        if (rowButton.DataContext is not ProxyEndpoint ep || string.IsNullOrWhiteSpace(ep.Ip))
            return;

        if (!RemovePreferredEdge(ep.Ip))
            return;

        await CommitProxyListChangeAsync();
        _log.Info($"已从代理列表移除 {ep.Ip}。");
    }

    /// <summary>从 PreferredEdges 按 IP 移除条目（大小写/空白容错）。</summary>
    private bool RemovePreferredEdge(string ip)
    {
        var edges = _settings.PreferredEdges;
        var existing = edges.FirstOrDefault(x => string.Equals(x.Ip?.Trim(), ip.Trim(), StringComparison.OrdinalIgnoreCase));
        return existing is not null && edges.Remove(existing);
    }

    /// <summary>代理列表发生变更后统一处理：刷新绑定、通知运行中的代理并持久化。</summary>
    private async Task CommitProxyListChangeAsync()
    {
        OnPropertyChanged(nameof(ProxyList));
        // 让运行中的代理立即感知列表变化，并落盘，否则重启后手动修改会丢失。
        _proxy?.UpdatePreferredEdges(_settings.PreferredEdges);
        RefreshCdnPoolCounts();
        await _settingsStore.SaveAsync(_settings);
    }

    // ------------------------------------------------------------------ 多 CDN 界面状态与网段来源

    /// <summary>按档位名找对应的网段来源（大小写不敏感）。</summary>
    private CdnRangeProvider? FindProviderFor(CdnProfile profile) =>
        _cdnProviders.FirstOrDefault(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>把各卡片上的「网段来源」同步给对应 Provider（Cloudflare 恒用官方双列表，覆盖不生效）。</summary>
    private void SyncRangeSources()
    {
        foreach (var profile in CdnProfiles)
        {
            var provider = FindProviderFor(profile);
            if (provider is not null)
                provider.RangeSourceUrl = profile.RangeSource;
        }
    }

    /// <summary>启动后静默预取全部 CDN 网段；单个来源失败只影响它自己（日志提示，沿用旧网段）。</summary>
    private async Task WarmUpRangeProvidersAsync()
    {
        try
        {
            SyncRangeSources();
            await Task.WhenAll(_cdnProviders.Select(p => p.RefreshAsync(CancellationToken.None)));
        }
        catch (Exception ex)
        {
            _log.Debug($"网段预热刷新未完成：{ex.Message}");
        }
    }

    /// <summary>
    /// 给代理列表的默认视图挂上按 CDN 的分组（幂等，_settings 被替换后需要重挂），
    /// 并绕开 WPF 一个坑：<b>DataGrid 声明了 GroupStyle 而当前一个 GroupItem 都没有</b>（＝列表空，或视图被筛空）时，
    /// 列头容器只量得出「各列 MinWidth 之和」，三列全塌成 20px——连固定 Width="90" 的那列也被挤，
    /// 所以空列表下 IP/地区码/操作 三个列名都显示不全。实测（542 宽）：空+挂着 20/20/20，空+摘掉 390/90/60，
    /// 有数据 362/90/60。空列表本来就没有分组表头可显示，所以空的时候摘掉、有数据再换回来，视觉无差别。
    /// </summary>
    private void EnsureProxyListGrouping()
    {
        var view = CollectionViewSource.GetDefaultView(ProxyList);
        if (!view.GroupDescriptions.Any(g => g is PropertyGroupDescription pgd && pgd.PropertyName == nameof(ProxyEndpoint.Cdn)))
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProxyEndpoint.Cdn)));

        // XAML 里那份 GroupStyle 只摘一次，存下来好换回去
        _proxyGroupStyle ??= ProxyGrid.GroupStyle.Count > 0 ? ProxyGrid.GroupStyle[0] : null;
        if (ProxyList.Count == 0)
        {
            ProxyGrid.GroupStyle.Clear();
        }
        else if (_proxyGroupStyle is not null && !ProxyGrid.GroupStyle.Contains(_proxyGroupStyle))
        {
            ProxyGrid.GroupStyle.Add(_proxyGroupStyle);
        }

        // 清空 / 删掉最后一行 / 优选结果灌进来时都要重算一遍；_settings 被整体替换后集合换了实例，得重挂
        if (ReferenceEquals(_groupedProxyList, ProxyList))
            return;
        if (_groupedProxyList is not null)
            _groupedProxyList.CollectionChanged -= OnProxyListChanged;
        _groupedProxyList = ProxyList;
        _groupedProxyList.CollectionChanged += OnProxyListChanged;
    }

    private void OnProxyListChanged(object? sender, NotifyCollectionChangedEventArgs e) => EnsureProxyListGrouping();

    /// <summary>按代理列表里的 CDN 归属刷新每个档位的池大小与状态行。</summary>
    private void RefreshCdnPoolCounts()
    {
        EnsureProxyListGrouping();
        var counts = _settings.PreferredEdges
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Cdn) ? "Cloudflare" : x.Cdn)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var p in CdnProfiles)
        {
            p.EdgeCount = counts.TryGetValue(p.Name, out var c) ? c : 0;
            if (p.StatusKind is "idle" or "ok")
            {
                if (!p.Enabled) { p.StatusKind = "idle"; p.StatusText = "未启用"; }
                else if (p.LastRunLocal is null) { p.StatusText = p.EdgeCount > 0 ? $"{p.EdgeCount} 个" : "未测速"; }
                else { p.StatusText = $"{p.EdgeCount} 个 · {p.LastRunText}"; }
            }
        }
    }


    /// <summary>结果表按 CDN 筛选（"全部" 不过滤）。</summary>
    private void CdnFilterComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TestResults is null) return;
        string selected = CdnFilterComboBox?.SelectedItem as string ?? "全部";
        Predicate<object>? filter = selected == "全部"
            ? null
            : o => o is IpResult r && r.Cdn == selected;
        CollectionViewSource.GetDefaultView(TestResults).Filter = filter;
    }

    /// <summary>
    /// 批量测速入口：对「勾选（Enabled）且已接入网段来源、测速地址有效」的档位，
    /// 逐个执行与「测速当前 CDN」完全相同的优选流程（复用 EdgeOptimizerV1，筛选逻辑不变）。
    /// 档位之间串行——测速内部本身已按 Routines/DownloadConcurrency 高并发，
    /// 并行跑多个 CDN 会互相争抢带宽、污染下载测速结果。
    /// 「停止」中断整批；被停止前已完整跑完的档位，其结果仍可确认保存。
    /// </summary>
    private async void BatchTest_Click(object sender, RoutedEventArgs e)
    {
        if (_optimizationCts is not null)
        {
            _optimizationCts.Cancel();
            return;
        }

        if (!TryReadSettings(out var settings))
            return;

        SyncRangeSources();

        // 只跑勾选且可用的档位；条件不满足的跳过并在日志说明原因。
        var queue = new List<(CdnProfile Profile, CdnRangeProvider Provider, AppSettings RunSettings)>();
        foreach (var profile in CdnProfiles.Where(p => p.Enabled))
        {
            var provider = FindProviderFor(profile);
            if (provider is null)
            {
                _log.Warn($"档位「{profile.Name}」未接入网段来源，已跳过。");
                continue;
            }
            // Cloudflare 档沿用全局校验过的地址；其余档位用卡片内存值（不持久化），单独校验并克隆进本次运行。
            var urlText = ReferenceEquals(profile, CdnProfiles[0])
                ? settings.SpeedTestUrl
                : (profile.SpeedTestUrl ?? string.Empty).Trim();
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            {
                _log.Warn($"档位「{profile.Name}」的测速地址无效，已跳过（请在「当前 CDN」卡片填写 HTTPS URL）。");
                continue;
            }
            var runSettings = ReferenceEquals(profile, CdnProfiles[0])
                ? settings
                : settings.CloneForRun(url.ToString());
            queue.Add((profile, provider, runSettings));
        }

        if (queue.Count == 0)
        {
            MessageBox.Show("没有可测速的档位：请勾选至少一个已接入网段来源、且测速地址有效的 CDN。",
                "批量测速", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings = settings;
        TestResults.Clear();
        _optimizationCts = new CancellationTokenSource();
        ProxyButton.IsEnabled = false;
        var winners = new List<(CdnProfile Profile, List<string> Selection)>();

        try
        {
            for (int i = 0; i < queue.Count; i++)
            {
                var (profile, provider, runSettings) = queue[i];
                provider.RangeSourceUrl = profile.RangeSource;

                SetTestingUi(true, $"批量测速中（{i + 1}/{queue.Count}：{profile.Name}）");
                _log.Info($"===== 批量测速 {i + 1}/{queue.Count}：{profile.Name} =====");
                profile.StatusKind = "run";
                profile.StatusText = "测速中…";

                var runCdn = profile; // 闭包捕获当前档位，给结果打 CDN 标签（与单档位测速一致）
                var progress = new Progress<IpResult>(result =>
                {
                    if (string.IsNullOrEmpty(result.Cdn) || result.Cdn == "Cloudflare")
                        result.Cdn = runCdn.Name;

                    var old = TestResults.FirstOrDefault(x => x.Ip == result.Ip);
                    if (old is not null)
                        TestResults.Remove(old);
                    TestResults.Add(result);
                });

                List<string> selection;
                try
                {
                    selection = await _optimizer.OptimizeAsync(provider, runSettings, progress, _optimizationCts.Token);
                }
                catch (OperationCanceledException)
                {
                    profile.StatusKind = profile.EdgeCount > 0 ? "ok" : "idle";
                    throw; // 停止作用于整批：交给外层统一收尾
                }
                catch (Exception ex)
                {
                    _log.Error($"{profile.Name} 优选失败：{ex.Message}");
                    profile.StatusKind = "err";
                    profile.StatusText = "测速失败";
                    continue; // 单个档位失败不影响其余档位
                }

                profile.LastRunLocal = DateTime.Now;
                if (selection.Count > 0)
                {
                    profile.StatusKind = "ok";
                    winners.Add((profile, selection));
                }
                else
                {
                    profile.StatusKind = "err";
                    profile.StatusText = "无可用IP";
                    _log.Warn($"{profile.Name}：本次优选未获得可用 IP。");
                }
            }

            if (winners.Count == 0)
                _log.Warn("批量测速结束：没有任何档位获得可用 IP。");
            else
                await ConfirmAndSaveBatchAsync(winners, "批量测速完成");
        }
        catch (OperationCanceledException)
        {
            _log.Warn("批量测速已停止。");
            // 停止前已完整跑完的档位仍然可以保存结果。
            if (winners.Count > 0)
                await ConfirmAndSaveBatchAsync(winners, "批量测速被停止，已完成档位有结果");
        }
        catch (Exception ex)
        {
            _log.Error($"批量测速失败：{ex.Message}");
            MessageBox.Show(ex.Message, "批量测速失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _optimizationCts.Dispose();
            _optimizationCts = null;
            ProxyButton.IsEnabled = !_proxyToggleBusy; // 启动/停止尚未结束时保持禁用，防重复点击
            SetTestingUi(false, "空闲");
            RefreshCdnPoolCounts();
        }
    }

    /// <summary>批量测速（含中途停止的部分结果）统一收尾：确认后按档位覆盖各自池子并持久化。</summary>
    private async Task ConfirmAndSaveBatchAsync(List<(CdnProfile Profile, List<string> Selection)> winners, string headline)
    {
        var summary = string.Join(" · ", winners.Select(w => $"{w.Profile.Name}：{w.Selection.Count} 个"));
        var confirm = MessageBox.Show(
            $"{headline}（{summary}）。\n\n" +
            "是否用本次结果覆盖代理列表中对应 CDN 的条目？（未参与的档位不受影响）",
            "覆盖确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (confirm != MessageBoxResult.Yes)
        {
            _log.Info("已取消覆盖，保留当前代理列表。");
            return;
        }

        foreach (var (profile, selection) in winners)
            OverwriteCdnPool(profile, selection);
        RefreshCdnPoolCounts();
        await _settingsStore.SaveAsync(_settings);
        ApplySavedEdges();
    }

    /// <summary>用一批优选结果覆盖指定档位在代理列表中的条目（其它 CDN 的池不动）。单档位与批量测速共用。</summary>
    private void OverwriteCdnPool(CdnProfile cdn, IReadOnlyList<string> selection)
    {
        var stale = _settings.PreferredEdges
            .Where(x => string.Equals(x.Cdn, cdn.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var old in stale)
            _settings.PreferredEdges.Remove(old);

        foreach (var ip in selection)
        {
            var hit = TestResults.FirstOrDefault(r => r.Ip == ip);
            _settings.PreferredEdges.Add(new ProxyEndpoint
            {
                Ip = ip,
                Colo = hit?.Colo ?? "",
                Cdn = cdn.Name,
            });
        }
        _proxy?.UpdatePreferredEdges(_settings.PreferredEdges);
        EnsureProxyListGrouping();
    }

    /// <summary>停止当前测速（复用优选流程已有的取消机制）。</summary>
    private void StopTest_Click(object sender, RoutedEventArgs e)
    {
        if (_optimizationCts is null)
        {
            _log.Debug("当前没有正在进行的测速。");
            return;
        }
        _optimizationCts.Cancel();
        BatchStatusText.Text = "正在停止…";
    }

    /// <summary>测速进行中/结束时的界面状态切换（只动按钮与进度条，不碰测速流程）。</summary>
    private void SetTestingUi(bool running, string? statusText = null)
    {
        OptimizeButton.IsEnabled = !running;
        RunSelectedButton.IsEnabled = !running;
        StopTestButton.IsEnabled = running;
        OverallProgress.IsIndeterminate = running;
        OverallProgress.Value = 0;
        if (statusText is not null)
            BatchStatusText.Text = statusText;
    }

    // ------------------------------------------------------------------ Toast 轻提示

    /// <summary>右下角弹一条 Toast（ToastHost 不接收命中测试，纯提示不打断操作）。见 <see cref="ToastNotifier"/>。</summary>
    private void Toast(string message, ToastNotifier.Kind kind = ToastNotifier.Kind.Info, int holdMs = 2600)
    {
        if (ToastHost is not null)
            ToastNotifier.Show(ToastHost, message, kind, holdMs);
    }
}
