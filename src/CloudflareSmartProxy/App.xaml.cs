using CloudflareSmartProxy.Services;

namespace CloudflareSmartProxy;

public partial class App : System.Windows.Application
{
    /// <summary>
    /// 本进程是不是主实例。false＝只是被双击的第二个副本，它必须**不碰任何共享状态**：
    /// 尤其 <see cref="OnExit"/> 里的 <see cref="SystemProxy.ClearProxy"/>——它进程内没有写入记录，
    /// 恰好会命中「值长得像我们写的就当成自己的清掉」那套启发式，把主实例正在用的系统代理给关了。
    /// </summary>
    private static bool _isPrimaryInstance = true;

    /// <summary>
    /// 提权子进程入口：主窗体的「管理员安装(全系统)」用 runas 重启自身并带上 --install-ca / --uninstall-ca。
    /// 这类调用只操作证书存储，做完立即以退出码结束，不创建主窗口（主程序本身仍以 asInvoker 运行）。
    /// </summary>
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        int caResult = CloudflareSmartProxy.MainWindow.HandleCertificateArguments(e.Args);
        if (caResult != int.MinValue)
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            Shutdown(caResult);
            return;   // 提权助手必须排在单实例闸门之前：那时主实例还在跑，拦下来证书就装不成了
        }

        // 单实例闸门。必须在 base.OnStartup 之前——那一句才会按 StartupUri 建主窗口。
        if (!SingleInstance.TryBecomePrimary())
        {
            _isPrimaryInstance = false;
            RevealExistingInstanceAndExit();
            return;
        }

        SingleInstance.WakeRequested += RevealMainWindow;
        SingleInstance.StartWakeListener();

        base.OnStartup(e);
    }

    /// <summary>
    /// 后来者：敲一下主实例的门让它把窗口带到前台（藏在托盘里时等效于点托盘图标），然后自己退出。
    /// 不在 UI 线程上阻塞等结果——这里没有可依赖的泵；联系不上才弹一句提示（多数情况是主实例正在退出）。
    /// </summary>
    private void RevealExistingInstanceAndExit()
    {
        _ = Task.Run(async () =>
        {
            bool reached = await SingleInstance.NotifyPrimaryAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                if (!reached)
                    System.Windows.MessageBox.Show(
                        "IPOptimize 已经在运行（窗口可能在右下角托盘里）。",
                        "已在运行", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                Shutdown(0);
            });
        });
    }

    /// <summary>把已存在的主窗口带到前台。窗口还没建起来（极早期的敲门）就什么都不做。</summary>
    private void RevealMainWindow()
    {
        Dispatcher.InvokeAsync(() =>
        {
            var window = MainWindow;
            if (window is CloudflareSmartProxy.MainWindow main)
                main.ShowFromTray();
        });
    }

    /// <summary>
    /// 进程退出前兜底关闭本程序设的系统代理。
    /// 正常关窗时 MainWindow_Closing 已经关过一次，这里调用是幂等的空操作（注册表里的值已被清掉，认不出是自己的）；
    /// 只有在 Closing 的异步停止没跑完（例如注销）时才会真正生效。
    /// 被任务管理器强杀时不会走到这里，系统代理会一直指向已关闭的本机端口，需要手动改回或在下次本程序退出时关闭。
    /// 被单实例闸门拒掉的第二个副本必须跳过这里（见 <see cref="_isPrimaryInstance"/>），否则会把主实例的代理关掉。
    /// </summary>
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_isPrimaryInstance)
        {
            try
            {
                SystemProxy.ClearProxy(out _);
            }
            catch
            {
                // 退出阶段抛异常没有意义。
            }
        }

        base.OnExit(e);
    }
}
