using System;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 服务层静态日志桥。DNS 扫描 / 竞速 / 证书这些服务不持有 <see cref="LogService"/> 实例
/// （LogService 依赖 Application.Current.Dispatcher，服务层反向依赖会把它们绑死在 UI 上），
/// 因此统一经这里转发；MainWindow 构造时注册 Sink，未注册时静默丢弃。
/// </summary>
public static class AppLog
{
    /// <summary>(级别, 消息) 的接收端；级别取值与 <see cref="LogService"/> 一致：INFO/WARN/ERROR/DEBUG。</summary>
    public static Action<string, string>? Sink;

    public static void Info(string message) => Sink?.Invoke("INFO", message);
    public static void Warn(string message) => Sink?.Invoke("WARN", message);
    public static void Error(string message) => Sink?.Invoke("ERROR", message);
    public static void Debug(string message) => Sink?.Invoke("DEBUG", message);

    public static void Error(string message, Exception ex)
        => Sink?.Invoke("ERROR", $"{message}: {ex.GetType().Name} {ex.Message}");
}

/// <summary>HTTP 协议数据非法（头部超限、格式错误等），连接应被关闭。</summary>
public sealed class MalformedHttpException : Exception
{
    public MalformedHttpException(string message) : base(message) { }
}

/// <summary>请求体超过缓冲上限，应退化为单连接流式转发。</summary>
public sealed class RequestBodyTooLargeException : Exception
{
    public RequestBodyTooLargeException(string message) : base(message) { }
}
