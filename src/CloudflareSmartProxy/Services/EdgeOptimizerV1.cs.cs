using CloudflareSmartProxy.Models;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Controls.Primitives;
using System.Runtime.CompilerServices;

namespace CloudflareSmartProxy.Services
{
    public class EdgeOptimizerV1
    {
        private readonly LogService _log;
        // 进度统计（多个并发任务共享）
        private long _pingTotalAttempts;
        private long _pingCompletedAttempts;
        private long _pingAvailableSuccesses;
        // 下载进度统计
        private long _downloadTotalAttempts;
        private long _downloadCompletedAttempts;
        private long _downloadAvailableSuccesses;
        private const int ProgressBarWidth = 30;
        private long _lastProgressMs;
        private long _lastDownloadProgressMs;
        private const int ProgressMinIntervalMs = 150;

        public EdgeOptimizerV1(LogService log)
        {
            _log = log;
        }

        public async Task<List<string>> OptimizeAsync(
        CdnRangeProvider ranges,
        AppSettings settings,
        IProgress<IpResult> progress,
        CancellationToken cancellationToken)
        {
            await ranges.RefreshAsync(cancellationToken);
            var speedUri = new Uri(settings.SpeedTestUrl);
            // 1) 构建候选 IP列表
            var candidates = ranges.BuildCandidates(settings.SamplesPerPrefix).ToList();

            _log.Info($"阶段 1/4：对 {ranges.Name} 的 {candidates.Count} 个候选 IP 执行 TCPing（{settings.Port} 端口）。");
            // 2) 并发延迟测速（TCPing）
            var pinged = await PingAsync(candidates, settings, cancellationToken);
            _log.Info("阶段 2/4：按成功率、延迟和抖动筛选。");
            // 3) 延迟 / 丢包过滤，按平均延迟升序
            var filtered = new List<IpResult>(pinged.Count);
            foreach (var r in pinged)
            {
                if (r.AvgDelayMs >= settings.MinDelayMs && r.AvgDelayMs <= settings.MaxDelayMs && r.LossRate <= settings.MaxLossRate)
                {
                    filtered.Add(r);
                    progress.Report(r);
                }
            }
            filtered.Sort((a, b) => a.AvgDelayMs.CompareTo(b.AvgDelayMs));


            _log.Info($"阶段 3/4：从 {filtered.Count} 个低延迟地址中下载测速。");

            await DownloadSetAsync(filtered, settings, progress, cancellationToken);

            _log.Info("阶段 4/4：优选IP挑选。");
            var speedSet = new List<IpResult>();
            if (settings.MinSpeedMBs > 0)
            {
                for (int i = 0; i < filtered.Count; i++)
                {
                    if (filtered[i].SpeedMBs >= settings.MinSpeedMBs)
                    {
                        speedSet.Add(filtered[i]);
                    }
                }
            }
            else
            {
                // 未指定下限：返回全部过滤后的 IP（未测速的下载速度为 0，排在最后）
                speedSet = filtered.ToList();
            }

            speedSet.Sort((a, b) => b.SpeedMBs.CompareTo(a.SpeedMBs)); // 按速度降序
            speedSet = speedSet.Take(settings.OptimizationCount).ToList();
            //todo 都没有下载速度的时候给出提示。
            _log.Info($"优选完成");
            return speedSet.Select(x => x.Ip).ToList();
        }
        /// <summary>
        /// 执行 Ping 测试
        /// </summary>
        /// <param name="ips"></param>
        /// <param name="opt"></param>
        /// <param name="progress"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        private async Task<List<IpResult>> PingAsync(List<string> ips, AppSettings settings, CancellationToken ct)
        {
            // 延迟测速只有 TCPing 一条路（原 HTTPing 分支连同「状态码 / 匹配地区码 / HTTP 超时 / 跳过证书校验」
            // 这几个设置项已整体删除），所以这里不再需要测速地址的主机名、路径与地区码白名单。
            // 初始化全局进度计数器
            Interlocked.Exchange(ref _pingCompletedAttempts, 0);
            Interlocked.Exchange(ref _pingAvailableSuccesses, 0);
            Interlocked.Exchange(ref _lastProgressMs, 0);
            _pingTotalAttempts = (long)ips.Count;
            _log.Progress(BuildProgressString());

            var results = new IpResult?[ips.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, ips.Count),
                new ParallelOptions { MaxDegreeOfParallelism = settings.Routines, CancellationToken = ct },
                async (i, t) =>
                {
                    results[i] = await TcpPingOneAsync(ips[i], settings, t);
                });

            var list = new List<IpResult>(ips.Count);
            foreach (var r in results)
                if (r != null) list.Add(r);
            return list;
        }
        /// <summary>TCPing：对端口做 PingTimes 次 TCP connect，统计成功次数与平均连接耗时。</summary>
        private async Task<IpResult?> TcpPingOneAsync(string ip, AppSettings settings, CancellationToken ct)
        {
            var endPoint = CreateEndPoint(ip, settings.Port);
            int recv = 0;
            long totalTicks = 0;
            for (int i = 0; i < settings.PingTimes; i++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(settings.ConnectTimeoutMs);
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    using var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    await socket.ConnectAsync(endPoint, cts.Token);
                    totalTicks += Stopwatch.GetTimestamp() - t0;
                    recv++;

                }
                catch
                {
                    // 超时或失败，本回合不计
                }
            }
            // 无论成功或失败，都更新已完成尝试并节流刷新进度显示
            Interlocked.Increment(ref _pingCompletedAttempts);
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastProgressMs);
            if (now - last >= ProgressMinIntervalMs)
            {
                if (Interlocked.CompareExchange(ref _lastProgressMs, now, last) == last)
                {
                    _log.Progress(BuildProgressString());
                }
            }
            if (recv == 0) return null;
            // 全局成功计数
            Interlocked.Increment(ref _pingAvailableSuccesses);

            return new IpResult
            {
                Ip = ip,
                Sended = settings.PingTimes,
                Received = recv,
                LossRate = 1.0 - (double)recv / settings.PingTimes,
                AvgDelayMs = TicksToMs(totalTicks) / recv
            };
        }

        private string BuildProgressString()
        {
            long total = Interlocked.Read(ref _pingTotalAttempts);
            long done = Interlocked.Read(ref _pingCompletedAttempts);
            long avail = Interlocked.Read(ref _pingAvailableSuccesses);
            if (total <= 0)
            {
                var empty = new string('-', ProgressBarWidth);
                return $"0 / 0 [{empty}] 可用: {avail}";
            }
            double pct = Math.Min(1.0, (double)done / total);
            int filled = (int)Math.Round(pct * ProgressBarWidth);
            if (filled < 0) filled = 0;
            if (filled > ProgressBarWidth) filled = ProgressBarWidth;
            var bar = new string('#', filled).PadRight(ProgressBarWidth, '-');
            return $"{done} / {total} [{bar}] 可用: {avail}";
        }

        private string BuildDownloadProgressString()
        {
            long total = Interlocked.Read(ref _downloadTotalAttempts);
            long done = Interlocked.Read(ref _downloadCompletedAttempts);
            long avail = Interlocked.Read(ref _downloadAvailableSuccesses);
            if (total <= 0)
            {
                var empty = new string('-', ProgressBarWidth);
                return $"0 / 0 [{empty}] 可用: {avail}";
            }
            double pct = Math.Min(1.0, (double)done / total);
            int filled = (int)Math.Round(pct * ProgressBarWidth);
            if (filled < 0) filled = 0;
            if (filled > ProgressBarWidth) filled = ProgressBarWidth;
            var bar = new string('#', filled).PadRight(ProgressBarWidth, '-');
            return $"{done} / {total} [{bar}] 可用: {avail}";
        }

        // ------------------------------------------------------------------ 下载测速 (download.go)

        private async Task DownloadSetAsync(List<IpResult> pinged, AppSettings settings, IProgress<IpResult> progress, CancellationToken ct)
        {
            int testNum = pinged.Count;
            if (testNum == 0) return;

            _log.Info($"开始下载测速（数量：{testNum}，下限：{settings.MinSpeedMBs:F2} MB/s，并发：{settings.DownloadConcurrency}）");
            // 初始化下载进度计数器并展示
            Interlocked.Exchange(ref _downloadCompletedAttempts, 0);
            Interlocked.Exchange(ref _downloadAvailableSuccesses, 0);
            Interlocked.Exchange(ref _lastDownloadProgressMs, 0);
            _downloadTotalAttempts = testNum;
            _log.Info("");
            _log.Progress(BuildDownloadProgressString());
            var speeds = new double[testNum];
            var colos = new string[testNum];

            await Parallel.ForEachAsync(Enumerable.Range(0, testNum),
                new ParallelOptions { MaxDegreeOfParallelism = settings.DownloadConcurrency, CancellationToken = ct },
                async (i, t) =>
                {
                    var (s, c) = await DownloadOneAsync(pinged[i].Ip, settings, t);
                    pinged[i].SpeedMBs = Math.Round(s / (1024.0 * 1024.0), 2);
                    if (c.Length > 0 && pinged[i].Colo.Length == 0) pinged[i].Colo = c;
                    if (s > 0)
                    {
                        Interlocked.Increment(ref _downloadAvailableSuccesses);
                        progress.Report(pinged[i]);
                    }
                    Interlocked.Increment(ref _downloadCompletedAttempts);
                    var now = Environment.TickCount64;
                    var last = Interlocked.Read(ref _lastDownloadProgressMs);
                    if (now - last >= ProgressMinIntervalMs &&
                        Interlocked.CompareExchange(ref _lastDownloadProgressMs, now, last) == last)
                        _log.Progress(BuildDownloadProgressString());
                });

            // 最终确保进度显示为完成状态
            Interlocked.Exchange(ref _downloadCompletedAttempts, _downloadTotalAttempts);
            _log.Progress(BuildDownloadProgressString());
        }

        /// <summary>
        /// 对单个 IP 做下载测速：GET 目标地址，支持 ≤10 次重定向，正文读取按时间片做 EWMA 平滑，
        /// 返回 (字节/秒, 地区码)。速度公式与 Go 版一致：ewma 值 / (DownloadTime/120)。
        /// </summary>
        public async Task<(double SpeedBps, string Colo)> DownloadOneAsync(string ip, AppSettings settings, CancellationToken ct)
        {
            string currentUrl = settings.SpeedTestUrl;
            string colo = "";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(settings.DownloadTime));

            for (int redirects = 0; redirects <= 10; redirects++)
            {
                var cur = new Uri(currentUrl);
                bool https = cur.Scheme == Uri.UriSchemeHttps;
                string path = cur.PathAndQuery.Length == 0 ? "/" : cur.PathAndQuery;
                RawConnection? conn = null;
                try
                {
                    conn = await OpenRawConnectionAsync(ip, settings.Port, cur.Host, https, deadline.Token);
                    await WriteRequestAsync(conn, "GET", cur.Authority, path, close: false, deadline.Token);
                    var head = await ReadHeadAsync(conn, deadline.Token);
                    if (head.StatusCode == 200)
                    {
                        colo = ExtractColo(head.Headers);
                        double speed = await ReadBodyEwmaAsync(conn, head, settings, deadline);
                        
                        return (speed, colo);
                    }
                    if (head.StatusCode >= 300 && head.StatusCode < 400 && redirects < 10
                        && head.Headers.TryGetValue("Location", out var loc) && !string.IsNullOrEmpty(loc))
                    {
                        currentUrl = new Uri(new Uri(currentUrl), loc).ToString();
                        continue; // 重新连接同一 IP，跟随重定向
                    }
                    //如果返回403，标识在当前CDN Edge节点上没有该网站的CDN缓存。
                    return (0, colo);
                }
                catch (Exception ex)
                {
                    return (0, colo);
                }
                finally
                {
                    conn?.Dispose();
                }
            }
            return (0, colo);
        }

        /// <summary>
        /// 读取响应体并计算实际平均下载速度（字节/秒）。
        /// 不对未完成的时间片进行外推，避免小文件/快速完成时产生虚高速度。
        /// </summary>
        private static async Task<double> ReadBodyEwmaAsync(
            RawConnection conn,
            HeadInfo head,
            AppSettings settings,
            CancellationTokenSource deadline)
        {
            long contentLength = -1;

            if (head.Headers.TryGetValue("Content-Length", out var cl)
                && long.TryParse(
                    cl,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var v))
            {
                contentLength = v;
            }

            long start = Stopwatch.GetTimestamp();

            byte[] buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
            long bytes = 0;

            try
            {
                while (true)
                {
                    int n;

                    try
                    {
                        n = await conn.ReadAsync(
                            buf.AsMemory(0, buf.Length),
                            deadline.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // 到达测速时间，使用已经收到的数据计算速度
                        break;
                    }

                    if (n <= 0)
                    {
                        // EOF
                        break;
                    }

                    bytes += n;

                    // 已经把整个响应体下载完成
                    if (contentLength >= 0 && bytes >= contentLength)
                    {
                        break;
                    }

                    // 达到测速时间
                    if (Stopwatch.GetTimestamp() >=
                        start + (long)(settings.DownloadTime * Stopwatch.Frequency))
                    {
                        break;
                    }
                }
            }
            catch
            {
                // 读取过程中出现异常：
                // 保留已经收到的数据，继续计算实际速度。
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }

            long stop = Stopwatch.GetTimestamp();

            double elapsedSec =
                (stop - start) / (double)Stopwatch.Frequency;

            // 防止除零
            if (elapsedSec <= 0 || bytes <= 0)
                return 0;
            // 实际平均下载速度：字节 / 秒
            return bytes / elapsedSec;
        }

        // ------------------------------------------------------------------ 底层连接与 HTTP 读写
        private IPEndPoint CreateEndPoint(string ip, int port) => new(IPAddress.Parse(ip), port);

        // ------------------------------------------------------------------ 地区码提取 (httping.go getHeaderColo)

        /// <summary>从响应头提取 CDN 地区码（兼容 Cloudflare / CDN77 / BunnyCDN / CloudFront / Fastly / Gcore）。</summary>
        private string ExtractColo(IReadOnlyDictionary<string, string> h)
        {
            if (h.TryGetValue("server", out var server))
            {
                if (server == "cloudflare")
                {
                    if (h.TryGetValue("cf-ray", out var cfRay)) return FirstUpperRun(cfRay, 3);
                }
                else if (server == "CDN77-Turbo")
                {
                    if (h.TryGetValue("x-77-pop", out var pop)) return FirstUpperRun(pop, 2);
                }
                else if (server.StartsWith("BunnyCDN-", StringComparison.Ordinal))
                {
                    return FirstUpperRun(server.Substring("BunnyCDN-".Length), 2);
                }
            }
            if (h.TryGetValue("x-amz-cf-pop", out var cfPop)) return FirstUpperRun(cfPop, 3);
            if (h.TryGetValue("x-served-by", out var servedBy))
            {
                string last = LastUpperRun3(servedBy);
                if (last.Length > 0) return last; // Fastly 可能有多个地区码，取最后一个
            }
            if (h.TryGetValue("x-id-fe", out var idFe))
            {
                var sb = new StringBuilder(2);
                foreach (char c in idFe)
                {
                    if (c >= 'a' && c <= 'z') { sb.Append(c); if (sb.Length == 2) break; }
                    else break;
                }
                if (sb.Length == 2) return sb.ToString().ToUpperInvariant(); // Gcore 城市码转大写
            }
            return "";
        }

        /// <summary>取第一个连续大写字母串，长度不足 need 时返回空串。</summary>
        private string FirstUpperRun(string s, int need)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] >= 'A' && s[i] <= 'Z')
                {
                    int j = i, count = 0;
                    while (j < s.Length && s[j] >= 'A' && s[j] <= 'Z' && count < need)
                    {
                        count++;
                        j++;
                    }
                    if (count == need) return s.Substring(i, need);
                    i = j - 1;
                }
            }
            return "";
        }

        /// <summary>取最后一个长度 ≥3 的连续大写字母串的前 3 位（Fastly x-served-by）。</summary>
        private string LastUpperRun3(string s)
        {
            string best = "";
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] >= 'A' && s[i] <= 'Z')
                {
                    int j = i;
                    while (j < s.Length && s[j] >= 'A' && s[j] <= 'Z') j++;
                    if (j - i >= 3) best = s.Substring(i, 3);
                    i = j - 1;
                }
            }
            return best;
        }

        /// <summary>建立到指定 IP 的原始连接；https 时附加 TLS（SNI 用目标主机名，始终校验证书，与 Go 版默认一致）。</summary>
        private async Task<RawConnection> OpenRawConnectionAsync(string ip, int port, string host, bool https, CancellationToken ct)
        {
            var endPoint = CreateEndPoint(ip, port);
            var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(endPoint, ct);
                Stream stream = new NetworkStream(socket, ownsSocket: true);
                if (https)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, ct);
                    stream = ssl;
                }
                return new RawConnection(stream, socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>发送 HTTP 请求行与请求头（ASCII）。</summary>
        private async Task WriteRequestAsync(RawConnection conn, string method, string host, string path, bool close, CancellationToken ct)
        {
            var sb = new StringBuilder(192);
            sb.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(host).Append("\r\n");
            sb.Append("User-Agent: Mozilla/5.0 (Macintosh; Intel Mac OS X 10_12_6) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/98.0.4758.80 Safari/537.36\r\n");
            if (close) sb.Append("Connection: close\r\n");
            sb.Append("\r\n");
            await conn.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        }

        /// <summary>读取响应头（状态行 + 全部响应头），返回状态码与头字典；多读的字节保存在连接的 surplus 中。</summary>
        private async Task<HeadInfo> ReadHeadAsync(RawConnection conn, CancellationToken ct)
        {
            byte[] buf = conn.HeadBuffer;
            int total = 0;
            while (total < buf.Length)
            {
                int n = await conn.ReadAsync(buf.AsMemory(total, buf.Length - total), ct);
                if (n <= 0) throw new IOException("读取响应头失败：连接被关闭");
                total += n;
                int term = FindHeadEnd(buf, total);
                if (term >= 0)
                {
                    int termLen = buf[term] == '\r' ? 4 : 2;
                    if (term + termLen < total)
                        conn.PushSurplus(term + termLen, total - term - termLen);
                    return ParseHead(buf, term);
                }
            }
            throw new IOException("响应头过大（>8KB）");
        }

        /// <summary>在缓冲区中定位响应头结束位置（支持 \r\n\r\n 与 \n\n）。返回结束符起始下标，未找到返回 -1。</summary>
        private int FindHeadEnd(byte[] buf, int count)
        {
            //int from = Math.Max(0, count - 5); // 结束符最长 4 字节，只需检查末尾窗口
            int from = 0;
            for (int i = from; i < count; i++)
            {
                if (buf[i] == '\n' && i + 1 < count && buf[i + 1] == '\n') return i;
                if (buf[i] == '\r' && i + 3 < count && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n') return i;
            }
            return -1;
        }

        private HeadInfo ParseHead(byte[] buf, int len)
        {
            var info = new HeadInfo();
            string head = Encoding.ASCII.GetString(buf, 0, len);
            int idx = head.IndexOf('\n');

            // 状态行：HTTP/1.1 200 OK
            string statusLine = idx < 0 ? head : head.Substring(0, idx).TrimEnd('\r');
            int sp = statusLine.IndexOf(' ');
            if (sp > 0)
            {
                int sp2 = statusLine.IndexOf(' ', sp + 1);
                if (sp2 < 0) sp2 = statusLine.Length;
                int.TryParse(statusLine.AsSpan(sp + 1, sp2 - sp - 1), NumberStyles.None, CultureInfo.InvariantCulture, out info.StatusCode);
            }

            // 响应头
            while (idx >= 0)
            {
                int next = head.IndexOf('\n', idx + 1);
                string line = next < 0 ? head.Substring(idx + 1) : head.Substring(idx + 1, next - idx - 1);
                line = line.TrimEnd('\r');
                if (line.Length == 0) break;
                int colon = line.IndexOf(':');
                if (colon > 0)
                    info.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                if (next < 0) break;
                idx = next;
            }
            return info;
        }
        private double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        private sealed class HeadInfo
        {
            public int StatusCode;
            public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        }
        private sealed class Ewma
        {
            private readonly double _alpha;
            private double _value;

            public Ewma(double alpha) => _alpha = alpha;

            public void Add(double v)
            {
                if (_value == 0.0) _value = v; // 首次直接赋值（与 ewma 包一致）
                else _value = _alpha * v + (1.0 - _alpha) * _value;
            }

            public double Value => _value;
        }
        /// <summary>
        /// 底层连接封装：持有 Socket 与（可选的 TLS）Stream，并维护一个 8KB 响应头缓冲。
        /// 读响应头时若多读了字节（如紧随其后的响应体），会作为 surplus 保存并在后续读取中优先消费。
        /// </summary>
        private sealed class RawConnection : IDisposable
        {
            private readonly Stream _stream;
            private readonly Socket _socket;
            private int _surplusOffset, _surplusEnd;
            public readonly byte[] HeadBuffer = new byte[8192];

            public RawConnection(Stream stream, Socket socket)
            {
                _stream = stream;
                _socket = socket;
            }

            public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                if (_surplusEnd > _surplusOffset)
                {
                    int n = Math.Min(buffer.Length, _surplusEnd - _surplusOffset);
                    HeadBuffer.AsMemory(_surplusOffset, n).CopyTo(buffer);
                    _surplusOffset += n;
                    return new ValueTask<int>(n);
                }
                return _stream.ReadAsync(buffer, ct);
            }

            public void PushSurplus(int offset, int count)
            {
                _surplusOffset = offset;
                _surplusEnd = offset + count;
            }

            public ValueTask WriteAsync(byte[] data, CancellationToken ct) => _stream.WriteAsync(data.AsMemory(), ct);

            public void Dispose()
            {
                try { _stream.Dispose(); }
                finally { _socket.Dispose(); }
            }
        }
    }
}
