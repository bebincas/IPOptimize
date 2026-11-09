using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 明文 HTTP（absolute-form）请求行的处理：把浏览器发给代理的形式改成发往源站的形式。
/// 这里只做字节层面的字符串手术，并按 Latin1 编解码（与 <see cref="HttpHead"/> 一致）：
/// 不走 <see cref="Uri"/> 的路径归一化，也不做 ASCII 降级——
/// 路径和查询里原本是什么字节，转给源站就是什么字节，否则中文或未预先百分号编码的 URL 会被改坏而 404。
/// </summary>
public static class ProxyProtocol
{
    private const string HttpScheme = "http://";

    /// <summary>CONNECT 的目标主机与端口（IPv6 形如 [::1]:443）。</summary>
    public static (string Host, int Port) ParseAuthority(string authority, int defaultPort)
    {
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0)
                throw new InvalidDataException("无效的 IPv6 地址");
            var host = authority[1..close];
            var port = close + 1 < authority.Length && authority[close + 1] == ':'
                ? int.Parse(authority[(close + 2)..]) : defaultPort;
            return (host, ValidatePort(port));
        }

        var colon = authority.LastIndexOf(':');
        if (colon > 0 && int.TryParse(authority[(colon + 1)..], out var parsedPort))
            return (authority[..colon], ValidatePort(parsedPort));
        return (authority, defaultPort);
    }

    /// <summary>转发一条明文请求所需的全部信息：源站地址 + 要发出的 origin-form 路径。</summary>
    public readonly record struct ForwardTarget(string Host, int Port, string OriginForm)
    {
        /// <summary>补 <c>Host</c> 头用的 authority：IPv6 加方括号，非 80 端口带 :port。</summary>
        public string AuthorityHeaderValue()
        {
            var host = Host.IndexOf(':') >= 0 ? "[" + Host + "]" : Host;
            return Port == 80 ? host : host + ":" + Port.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 解析 <c>http://host[:port]/path?query</c> 形式的代理请求目标。
    /// 返回 false 表示不是 http 绝对形式（https 走 CONNECT，origin-form 是畸形用法），调用方按 400 处理。
    /// </summary>
    public static bool TryParseForwardTarget(string target, out ForwardTarget forward)
    {
        forward = default;
        if (target.Length <= HttpScheme.Length ||
            !target.StartsWith(HttpScheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = target.AsSpan(HttpScheme.Length);
        int cut = rest.IndexOfAny('/', '?', '#');
        string authority;
        string originForm;
        if (cut < 0)
        {
            authority = rest.ToString();
            originForm = "/";
        }
        else if (rest[cut] == '/')
        {
            authority = rest[..cut].ToString();
            originForm = rest[cut..].ToString();
        }
        else
        {
            // http://host?x=1 这类没有路径的写法：请求行必须补一个 '/'
            authority = rest[..cut].ToString();
            originForm = "/" + rest[cut..].ToString();
        }

        int at = authority.LastIndexOf('@');
        if (at >= 0)
            authority = authority[(at + 1)..];   // user:pass@host —— 浏览器不会发，畸形请求兜住
        if (authority.Length == 0)
            return false;

        string host;
        int port = 80;
        if (authority[0] == '[')
        {
            int close = authority.IndexOf(']');
            if (close < 0)
                return false;
            host = authority.Substring(1, close - 1);
            if (close + 1 < authority.Length)
            {
                if (authority[close + 1] != ':' ||
                    !int.TryParse(authority.Substring(close + 2), NumberStyles.None, CultureInfo.InvariantCulture, out port))
                    return false;
            }
        }
        else
        {
            int colon = authority.LastIndexOf(':');
            if (colon > 0 &&
                int.TryParse(authority.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                host = authority.Substring(0, colon);
                port = parsed;
            }
            else
            {
                host = authority;
            }
        }

        if (host.Length == 0 || port is < 1 or > 65535)
            return false;
        if (!IPAddress.TryParse(host, out _))
            host = EncodeIdnHost(host);

        forward = new ForwardTarget(host, port, originForm);
        return true;
    }

    /// <summary>
    /// 请求体的帧语义是否可疑：出现多个 Content-Length，或同时给 Content-Length 和 chunked。
    /// 这两种写法都是请求走私的特征，转发过去会让源站和代理对「一个请求」的理解不一致，直接拒掉。
    /// </summary>
    public static bool HasAmbiguousBodyFraming(HttpHead head)
    {
        int contentLengths = 0;
        foreach (var (name, _) in head.RawHeaders)
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLengths++;

        if (contentLengths > 1)
            return true;
        return contentLengths > 0 && head.IsChunked;
    }

    /// <summary>
    /// 重建发往源站的请求头：请求行换成 origin-form，丢掉逐跳/代理专用头，缺 Host 时补一个，
    /// 并按「一请求一连接」告诉源站 <c>Connection: close</c>（每条请求都要重新选路，上游连接不复用）。
    /// </summary>
    public static byte[] BuildForwardRequestHead(HttpHead head, ForwardTarget target)
    {
        var startLine = head.StartLine;
        int sp1 = startLine.IndexOf(' ');
        int sp2 = sp1 < 0 ? -1 : startLine.IndexOf(' ', sp1 + 1);
        var method = sp1 <= 0 ? head.Method : startLine[..sp1];
        var version = sp2 < 0 ? "HTTP/1.1" : startLine[(sp2 + 1)..];

        var sb = new StringBuilder(512);
        sb.Append(method).Append(' ').Append(target.OriginForm).Append(' ').Append(version).Append("\r\n");

        bool hasUpgrade = head.HasHeader("Upgrade");
        bool hasHost = false;
        foreach (var (name, value) in head.RawHeaders)
        {
            if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase)) continue;           // 100-continue 由代理自己回，不等源站
            if (name.Equals("TE", StringComparison.OrdinalIgnoreCase)) continue;               // 请求侧的传输扩展不外传
            if (!hasUpgrade && name.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) hasHost = true;
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        if (!hasHost)
            sb.Append("Host: ").Append(target.AuthorityHeaderValue()).Append("\r\n");

        sb.Append(hasUpgrade ? "Connection: Upgrade\r\n" : "Connection: close\r\n");
        sb.Append("\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// 客户端是否希望这条到代理的连接继续复用：HTTP/1.1 默认为是（显式 close 才否），HTTP/1.0 相反。
    /// </summary>
    public static bool WantsKeepAlive(HttpHead head)
    {
        var connection = head.Header("Connection") ?? head.Header("Proxy-Connection");
        bool http11 = head.StartLine.EndsWith("HTTP/1.1", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(connection))
            return http11;
        bool close = connection.Contains("close", StringComparison.OrdinalIgnoreCase);
        bool keepAlive = connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);
        return http11 ? !close : keepAlive;
    }

    // ------------------------------------------------------------------ 内部实现

    private static int ValidatePort(int port) => port is >= 1 and <= 65535
        ? port : throw new InvalidDataException("端口超出有效范围");

    /// <summary>
    /// 主机名转成 DNS/连接用的形式：ASCII 小写；含非 ASCII 字节（Latin1 解出来的 UTF-8 裸字节）时还原成
    /// Unicode 再交给 <see cref="Uri"/> 做 punycode 转换，避免中文域名被当成乱码查不到。
    /// 现代浏览器一般直接发 punycode，这里只是兜住 curl 这类发原始 Unicode 的客户端。
    /// </summary>
    private static string EncodeIdnHost(string host)
    {
        bool asciiOnly = true;
        foreach (var c in host)
            if (c > 127) { asciiOnly = false; break; }
        if (asciiOnly)
            return host.ToLowerInvariant();

        try
        {
            var unicode = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(host));
            if (Uri.TryCreate("http://" + unicode + "/", UriKind.Absolute, out var parsed) &&
                !string.IsNullOrEmpty(parsed.IdnHost))
                return parsed.IdnHost;
        }
        catch (UriFormatException)
        {
            // 非法主机名：原样交给 DNS，让它自己失败
        }
        return host;
    }
}
