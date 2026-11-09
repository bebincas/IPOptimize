using System;
using System.Collections.Generic;
using System.Text;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 解析后的 HTTP 头（请求头或响应头），保留原始字节与头顺序。
/// </summary>
public sealed class HttpHead
{
    public required string StartLine { get; init; }
    public required List<(string Name, string Value)> RawHeaders { get; init; }
    public required byte[] RawBytes { get; init; }

    public string? Header(string name)
    {
        foreach (var (n, v) in RawHeaders)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    public bool HasHeader(string name) => Header(name) is not null;

    // —— 请求侧 ——
    public string Method
    {
        get
        {
            var sp = StartLine.IndexOf(' ');
            return sp <= 0 ? StartLine : StartLine[..sp];
        }
    }

    public string RequestTarget
    {
        get
        {
            var sp1 = StartLine.IndexOf(' ');
            var sp2 = sp1 < 0 ? -1 : StartLine.IndexOf(' ', sp1 + 1);
            return sp1 < 0 ? "/" : sp2 < 0 ? StartLine[(sp1 + 1)..] : StartLine[(sp1 + 1)..sp2];
        }
    }

    public bool IsConnect => string.Equals(Method, "CONNECT", StringComparison.OrdinalIgnoreCase);

    // —— 通用 ——
    public bool IsResponse => StartLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase);

    public int StatusCode
    {
        get
        {
            var parts = StartLine.Split(' ');
            return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : 0;
        }
    }

    public long? ContentLength
    {
        get
        {
            var v = Header("Content-Length");
            return v is not null && long.TryParse(v.Trim(), out var len) && len >= 0 ? len : null;
        }
    }

    public bool IsChunked
    {
        get
        {
            var v = Header("Transfer-Encoding");
            return v is not null && v.Contains("chunked", StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>
/// HTTP/1.1 头部解析器（独立类，便于单测）。
/// </summary>
public static class HttpHeadParser
{
    public static HttpHead? TryParse(ReadOnlySpan<byte> headBytes)
    {
        if (headBytes.Length == 0) return null;
        var text = Encoding.Latin1.GetString(headBytes);
        // 去掉结尾的 \r\n\r\n 后按行拆
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        var startLine = lines[0].TrimEnd();
        if (startLine.Length == 0 || !startLine.Contains(' ')) return null;

        var headers = new List<(string, string)>();
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0) continue; // 容忍畸形行
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Length > 0) headers.Add((name, value));
        }
        return new HttpHead { StartLine = startLine, RawHeaders = headers, RawBytes = headBytes.ToArray() };
    }
}
