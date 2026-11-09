using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 只读前缀拼接流：先吐完已缓冲的 <c>prefix</c> 字节，再透传底层流。
/// 用途：客户端把 CONNECT 请求和紧随其后的 TLS ClientHello 写在同一个 TCP 包里时，
/// 代理读完请求头会剩下一段字节；做 MITM 时要让 <see cref="System.Net.Security.SslStream"/>
/// 先看到这段字节，否则握手必然失败。
/// </summary>
internal sealed class PrefixedStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _prefix;
    private int _prefixPos;

    public PrefixedStream(Stream inner, byte[] prefix)
    {
        _inner = inner;
        _prefix = prefix;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        if (_prefixPos < _prefix.Length)
        {
            int take = Math.Min(destination.Length, _prefix.Length - _prefixPos);
            _prefix.AsSpan(_prefixPos, take).CopyTo(destination);
            _prefixPos += take;
            return take;
        }
        return _inner.Read(destination);
    }

    public override int ReadByte()
    {
        if (_prefixPos < _prefix.Length)
            return _prefix[_prefixPos++];
        return _inner.ReadByte();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (_prefixPos < _prefix.Length)
        {
            int take = Math.Min(destination.Length, _prefix.Length - _prefixPos);
            _prefix.AsMemory(_prefixPos, take).CopyTo(destination);
            _prefixPos += take;
            return take;
        }
        return await _inner.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();   // 与调用方对底层流的处置保持一致：由本流负责关闭
        base.Dispose(disposing);
    }
}
