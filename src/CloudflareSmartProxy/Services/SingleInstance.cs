using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 单实例闸门：第一个进程占住命名 Mutex 当主实例；后来者先短暂等一次（治「上一个被强杀、锁没人解」的僵尸状态），
/// 等不到就敲门让主实例把窗口带到前台，自己安静退出。
///
/// 为什么要限死：HKCU 的系统代理和 127.0.0.1:&lt;端口&gt; 都是**整机一份**的资源——两个副本会抢同一个端口，
/// 后启动的 SetProxy 直接覆盖前一个写的值；而前一个退出时按「注册表里的值和我写过的一样」那套启发式认领，
/// 会把别人的那份当成自己的清掉（它自己进程内没有写入记录时更明显）。settings.json 同理是共享文件。
///
/// 注意：<c>--install-ca</c> / <c>--uninstall-ca</c> 那类提权自重启的子进程**不能**走这道闸门，
/// 它们由 <see cref="App"/> 在更早的分支处理完就退出（主实例还在跑，拦下来就装不了证书）。
/// </summary>
public static class SingleInstance
{
    /// <summary>Global\ ＝ 跨登录会话也只有一个：端口和 HKCU 代理配置在快速用户切换下同样会互相踩。</summary>
    private const string MutexName = @"Global\IPOptimize.SingleInstance";

    private const string WakePipeName = "IPOptimize.Wake";
    private const string WakeToken = "SHOW";

    /// <summary>后来者确认「前一个到底还活着吗」的等待窗口：期间主实例退出的话，锁会自然归我们。</summary>
    private static readonly TimeSpan PrimaryWait = TimeSpan.FromSeconds(2);

    /// <summary>静态持有：互斥体的所有权挂在内核句柄上，但如果托管对象先被回收，Finalizer 会关句柄＝白锁。</summary>
    private static Mutex? _mutex;

    /// <summary>收到后来者的「把窗口带出来」请求。在后台线程上触发，订阅方自己回 UI 线程。</summary>
    public static event Action? WakeRequested;

    /// <summary>
    /// 本进程是否应当作为唯一实例继续跑。返回 false ＝ 已有主实例：调用方必须立刻收工，
    /// 且不碰任何共享状态（系统代理、settings.json、监听端口）。
    /// </summary>
    public static bool TryBecomePrimary()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (createdNew)
                return true;

            try
            {
                // 上一个还持有：等一小会儿，它要是期间退出（或已经死了）锁就归我们
                if (_mutex.WaitOne(PrimaryWait))
                    return true;
            }
            catch (AbandonedMutexException)
            {
                // 上一个进程没解锁就死了（强杀/崩溃）：系统已把所有权交给我们，正常当主实例继续
                return true;
            }
            return false;
        }
        catch (Exception)
        {
            // 名字被占用且 ACL 不让动之类的怪事：不该把用户挡在程序外面，放行（端口冲突那侧已有兜底）
            return true;
        }
    }

    /// <summary>主实例调用：挂一个后台管道服务，专门接后来者的「把窗口带出来」请求。</summary>
    public static void StartWakeListener()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    // MaxNumberOfServerInstances=1：一次只接一个敲门者，处理完再开下一轮
                    using var server = new NamedPipeServerStream(
                        WakePipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync().ConfigureAwait(false);

                    var buf = new byte[WakeToken.Length + 1];
                    int n = 0;
                    while (n < buf.Length)
                    {
                        int read = await server.ReadAsync(buf.AsMemory(n, buf.Length - n)).ConfigureAwait(false);
                        if (read == 0) break;   // 敲门者没写东西就断了
                        n += read;
                    }
                    if (Encoding.ASCII.GetString(buf, 0, n) == WakeToken)
                        WakeRequested?.Invoke();
                }
                catch (Exception)
                {
                    // 单轮失败不影响服务本身；稍等一下再来，避免出错时空转打满 CPU
                    try { await Task.Delay(250).ConfigureAwait(false); }
                    catch (Exception) { }
                }
            }
        });
    }

    /// <summary>后来者调用：让主实例把自己的窗口带到前台。false＝没联系上（主实例可能正在退出）。</summary>
    public static async Task<bool> NotifyPrimaryAsync()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", WakePipeName, PipeDirection.Out);
            await client.ConnectAsync(1500).ConfigureAwait(false);
            await client.WriteAsync(Encoding.ASCII.GetBytes(WakeToken)).ConfigureAwait(false);
            await client.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
