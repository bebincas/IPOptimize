using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CloudflareSmartProxy.Services;

/// <summary>一次 A 记录查询的应答。</summary>
public sealed record DnsAAnswer(System.Collections.Generic.IReadOnlyList<IPAddress> Ips, uint TtlSeconds)
{
    public static readonly DnsAAnswer Empty = new(Array.Empty<IPAddress>(), 0);
}

/// <summary>
/// 极简 DNS over UDP 报文构建/解析（仅支持 A 记录查询，足够本工具使用）。
/// 不依赖任何第三方库：手写查询报文、跳过压缩指针、校验事务 ID 与 RCODE，取所有 A 记录的最小 TTL。
/// </summary>
public static class DnsMessage
{
    private const ushort QTYPE_A = 1;
    private const ushort QCLASS_IN = 1;

    /// <summary>构造标准递归 A 查询报文。</summary>
    public static byte[] BuildQueryA(ushort transactionId, string domain)
    {
        var ms = new MemoryStream(64);
        WriteU16(ms, transactionId);
        WriteU16(ms, 0x0100);          // 标准查询, RD=1
        WriteU16(ms, 1);               // QDCOUNT
        WriteU16(ms, 0);               // ANCOUNT
        WriteU16(ms, 0);               // NSCOUNT
        WriteU16(ms, 0);               // ARCOUNT

        foreach (var label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length == 0 || bytes.Length > 63)
                throw new ArgumentException($"非法域名标签: {label}", nameof(domain));
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        ms.WriteByte(0);               // 根标签
        WriteU16(ms, QTYPE_A);
        WriteU16(ms, QCLASS_IN);
        return ms.ToArray();
    }

    /// <summary>
    /// 解析 A 应答。<paramref name="queryId"/> 不匹配、非应答、RCODE 非零时抛异常；
    /// 报文过短/答案区解析不完整时返回已解析部分（容忍 TCP 截断）。
    /// </summary>
    public static DnsAAnswer ParseAResponse(ReadOnlySpan<byte> data, ushort queryId)
    {
        if (data.Length < 12)
            throw new InvalidDataException("DNS 应答过短");

        var id = ReadU16(data, 0);
        if (id != queryId)
            throw new InvalidDataException($"DNS 事务 ID 不匹配: {id} != {queryId}");

        var flags = ReadU16(data, 2);
        var isResponse = (flags & 0x8000) != 0;
        if (!isResponse)
            throw new InvalidDataException("收到的是查询而非应答");

        var rcode = flags & 0x000F;
        if (rcode != 0)
            throw new InvalidDataException($"DNS RCODE={rcode} (3=域名不存在, 2=服务器故障)");

        var qd = ReadU16(data, 4);
        var an = ReadU16(data, 6);

        var pos = 12;
        for (int q = 0; q < qd; q++)
        {
            pos = SkipName(data, pos);
            pos += 4;                   // QTYPE + QCLASS
            if (pos > data.Length) throw new InvalidDataException("DNS 问题区越界");
        }

        var ips = new System.Collections.Generic.List<IPAddress>(an);
        uint minTtl = uint.MaxValue;
        for (int a = 0; a < an && pos < data.Length; a++)
        {
            pos = SkipName(data, pos);
            if (pos + 10 > data.Length) break;   // 容忍截断（TC 位）
            var type = ReadU16(data, pos);
            var ttl = ReadU32(data, pos + 4);
            var rdLength = ReadU16(data, pos + 8);
            pos += 10;
            if (type == QTYPE_A && rdLength == 4 && pos + 4 <= data.Length)
            {
                ips.Add(new IPAddress(data.Slice(pos, 4).ToArray()));
                minTtl = Math.Min(minTtl, ttl);
            }
            pos += rdLength;
        }

        return ips.Count == 0
            ? DnsAAnswer.Empty
            : new DnsAAnswer(ips, minTtl == uint.MaxValue ? 0 : minTtl);
    }

    /// <summary>跳过一个（可能压缩指针的）域名，返回读取结束位置。</summary>
    private static int SkipName(ReadOnlySpan<byte> d, int pos)
    {
        while (true)
        {
            if (pos >= d.Length) return pos;
            var len = d[pos];
            if (len == 0) return pos + 1;
            if ((len & 0xC0) == 0xC0) return Math.Min(d.Length, pos + 2); // 压缩指针
            pos += 1 + len;
        }
    }

    public static ushort RandomTransactionId()
    {
        Span<byte> b = stackalloc byte[2];
        RandomNumberGenerator.Fill(b);
        return (ushort)((b[0] << 8) | b[1]);
    }

    private static void WriteU16(MemoryStream ms, ushort v)
    {
        ms.WriteByte((byte)(v >> 8));
        ms.WriteByte((byte)(v & 0xFF));
    }

    private static ushort ReadU16(ReadOnlySpan<byte> d, int i) => (ushort)((d[i] << 8) | d[i + 1]);
    private static uint ReadU32(ReadOnlySpan<byte> d, int i)
        => (uint)((d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3]);
}
