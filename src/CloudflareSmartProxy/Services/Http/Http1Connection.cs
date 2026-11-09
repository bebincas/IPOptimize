using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 带内部缓冲的 HTTP/1.1 连接读写器：支持头/行读取、精确字节数中继、
/// chunked 帧中继、直到关闭的原始泵。头部/行超限抛 <see cref="MalformedHttpException"/>。
/// </summary>
public sealed class Http1Connection
{
    private const int MaxHeadBytes = 64 * 1024;
    private const int MaxLineBytes = 32 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _buf = new byte[64 * 1024];
    private int _pos, _len;   // _buf[_pos.._len) 为未消费数据

    public Http1Connection(Stream stream)
    {
        _stream = stream;
    }

    public Stream Stream => _stream;

    // ============================ 写 ============================

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        => await _stream.WriteAsync(data, ct);

    public async Task FlushAsync(CancellationToken ct = default) => await _stream.FlushAsync(ct);

    public async Task WriteRawStringAsync(string s, CancellationToken ct = default)
    {
        var bytes = Encoding.Latin1.GetBytes(s);
        await _stream.WriteAsync(bytes, ct);
        await _stream.FlushAsync(ct);
    }

    public async Task WriteLineAsync(Stream dst, string line, CancellationToken ct = default)
    {
        var bytes = Encoding.Latin1.GetBytes(line + "\r\n");
        await dst.WriteAsync(bytes, ct);
    }

    // ============================ 读头/读行 ============================

    /// <summary>读取请求头/响应头（至 \r\n\r\n）；对端在发出完整头之前关闭返回 null。</summary>
    public async Task<HttpHead?> ReadHeadAsync(CancellationToken ct = default)
    {
        int searchFrom = _pos;
        while (true)
        {
            var idx = FindCrlfCrlf(searchFrom);
            if (idx >= 0)
            {
                var headBytes = new byte[idx + 4 - _pos];
                Array.Copy(_buf, _pos, headBytes, 0, headBytes.Length);
                _pos = idx + 4;
                var head = HttpHeadParser.TryParse(headBytes);
                if (head is null) throw new MalformedHttpException("HTTP 头格式非法");
                return head;
            }
            searchFrom = Math.Max(_pos, _len - 3);

            // 压缩缓冲，保证有追加空间（同步移动 searchFrom）
            if (_pos > 0)
            {
                var shift = _pos;
                Array.Copy(_buf, shift, _buf, 0, _len - shift);
                _len -= shift;
                _pos = 0;
                searchFrom -= shift;
            }
            if (_len == _buf.Length)
                throw new MalformedHttpException($"HTTP 头超过 {MaxHeadBytes / 1024}KB 限制");

            int n = await _stream.ReadAsync(_buf.AsMemory(_len, _buf.Length - _len), ct);
            if (n == 0)
                return _pos == _len ? null : throw new IOException("连接在 HTTP 头完成前关闭");
            _len += n;
        }
    }

    /// <summary>读一行（不含 CRLF）；对端关闭且无数据返回 null。</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder(128);
        while (true)
        {
            int idx = -1;
            for (int i = _pos; i < _len; i++)
                if (_buf[i] == (byte)'\n') { idx = i; break; }

            if (idx >= 0)
            {
                int lineEnd = idx;
                if (lineEnd > _pos && _buf[lineEnd - 1] == (byte)'\r') lineEnd--;
                var rest = Encoding.Latin1.GetString(_buf, _pos, lineEnd - _pos);
                _pos = idx + 1;
                return sb.Length == 0 ? rest : sb.Append(rest).ToString();
            }

            if (_len > _pos)
            {
                sb.Append(Encoding.Latin1.GetString(_buf, _pos, _len - _pos));
                _pos = _len;
            }
            if (sb.Length > MaxLineBytes)
                throw new MalformedHttpException($"HTTP 行超过 {MaxLineBytes / 1024}KB 限制");

            _pos = _len = 0; // 全部消费，重置
            int n = await _stream.ReadAsync(_buf, ct);
            if (n == 0)
                return sb.Length == 0 ? null : throw new IOException("连接在 HTTP 行完成前关闭");
            _len = n;
        }
    }

    /// <summary>
    /// 交出内部缓冲里「已读进内存但还没消费」的字节并清空缓冲。
    /// CONNECT 建隧道时要用：浏览器常把 CONNECT 和紧随其后的第一段 ClientHello 写进同一个包，
    /// 请求头消费完剩下的那段字节必须先交给下游，否则 TLS 握手少头。
    /// </summary>
    public byte[] TakeBuffered()
    {
        if (_pos >= _len) return Array.Empty<byte>();
        var bytes = new byte[_len - _pos];
        Array.Copy(_buf, _pos, bytes, 0, bytes.Length);
        _pos = 0;
        _len = 0;
        return bytes;
    }

    private int FindCrlfCrlf(int from)
    {
        var start = Math.Max(from, _pos);
        for (int i = start; i <= _len - 4; i++)
            if (_buf[i] == 13 && _buf[i + 1] == 10 && _buf[i + 2] == 13 && _buf[i + 3] == 10)
                return i;
        return -1;
    }

    // ============================ 读数据 ============================

    /// <summary>读数据（优先消费内部缓冲的剩余字节）。</summary>
    public async Task<int> ReadAsync(Memory<byte> dst, CancellationToken ct = default)
    {
        if (dst.Length == 0) return 0;
        if (_pos < _len)
        {
            int n = Math.Min(dst.Length, _len - _pos);
            _buf.AsSpan(_pos, n).CopyTo(dst.Span);
            _pos += n;
            return n;
        }
        return await _stream.ReadAsync(dst, ct);
    }

    /// <summary>精确读取 count 字节（可用于请求体缓冲，调用方保证不超限）。</summary>
    public async Task<byte[]> ReadExactAsync(long count, CancellationToken ct = default)
    {
        if (count is < 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(count));
        var result = new byte[count];
        int off = 0;
        while (off < count)
        {
            int n = await ReadAsync(result.AsMemory(off, (int)count - off), ct);
            if (n == 0) throw new IOException("连接提前关闭（读取请求体不完整）");
            off += n;
        }
        return result;
    }

    // ============================ 中继 ============================

    /// <summary>精确中继 count 字节到 dst（响应体有 Content-Length 时）。</summary>
    public async Task RelayFixedAsync(Stream dst, long count, CancellationToken ct = default)
    {
        var buffer = new byte[32 * 1024];
        long remaining = count;
        while (remaining > 0)
        {
            int take = (int)Math.Min(buffer.Length, remaining);
            int n = await ReadAsync(buffer.AsMemory(0, take), ct);
            if (n == 0) throw new IOException("上游响应体不完整");
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            remaining -= n;
        }
        await dst.FlushAsync(ct);
    }

    /// <summary>chunked 响应原帧中继（保持分块语义，可正确支持 keep-alive）。</summary>
    public async Task RelayChunkedAsync(Stream dst, CancellationToken ct = default)
    {
        while (true)
        {
            var line = await ReadLineAsync(ct)
                ?? throw new IOException("上游在 chunk 头处关闭连接");
            await WriteLineAsync(dst, line, ct);

            var sizeHex = line.Split(';', 2)[0].Trim();
            if (!long.TryParse(sizeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size))
                throw new MalformedHttpException($"非法 chunk 大小: {sizeHex}");

            if (size == 0)
            {
                // trailer 区，直到空行
                while (true)
                {
                    var t = await ReadLineAsync(ct) ?? "";
                    await WriteLineAsync(dst, t, ct);
                    if (t.Length == 0) break;
                }
                await dst.FlushAsync(ct);
                return;
            }

            await RelayFixedAsync(dst, size, ct);
            var crlf = await ReadExactAsync(2, ct); // chunk 数据后的 CRLF
            await dst.WriteAsync(crlf, ct);
        }
    }

    /// <summary>读到对端关闭为止（未知长度响应体 / 隧道）。</summary>
    public async Task RelayPumpAsync(Stream dst, CancellationToken ct = default)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            int n = await ReadAsync(buffer, ct);
            if (n == 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            await dst.FlushAsync(ct);
        }
    }

    /// <summary>把 chunked 请求体整体去块缓冲（超过 cap 抛 <see cref="RequestBodyTooLargeException"/>）。</summary>
    public async Task<byte[]> ReadChunkedBodyAsync(int cap, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var line = await ReadLineAsync(ct)
                ?? throw new IOException("连接在 chunk 头处关闭");
            var sizeHex = line.Split(';', 2)[0].Trim();
            if (!long.TryParse(sizeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size))
                throw new MalformedHttpException($"非法 chunk 大小: {sizeHex}");

            if (size == 0)
            {
                while ((await ReadLineAsync(ct)) is { Length: > 0 }) { } // trailer 区
                await FlushAsync(ct);
                return ms.ToArray();
            }
            if (ms.Length + size > cap)
                throw new RequestBodyTooLargeException($"chunked 请求体超过 {cap} 字节缓冲上限");

            var data = await ReadExactAsync(size, ct);
            ms.Write(data, 0, data.Length);
            await ReadExactAsync(2, ct); // CRLF
        }
    }
}
