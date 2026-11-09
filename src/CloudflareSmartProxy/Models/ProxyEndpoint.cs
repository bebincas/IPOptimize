using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudflareSmartProxy.Models;

/// <summary>
/// 代理列表条目：优选的 Cloudflare Edge IP + 其 CDN 地区码（Colo，仅用于界面显示与持久化）。
/// </summary>
[JsonConverter(typeof(ProxyEndpointConverter))]
public sealed class ProxyEndpoint
{
    public string Ip { get; set; } = string.Empty;

    /// <summary>CDN 归属，用于代理列表分组与各池计数。旧数据默认为 Cloudflare。</summary>
    public string Cdn { get; set; } = "Cloudflare";

    /// <summary>CDN 地区码（IATA 三字码/国家码等），可能为空。</summary>
    public string Colo { get; set; } = string.Empty;

    public override string ToString() => string.IsNullOrEmpty(Colo) ? Ip : $"{Ip} ({Colo})";
}

/// <summary>
/// 兼容旧数据：早期 settings.json 里 PreferredEdges 是纯字符串数组（只有 IP）。
/// 读取时既能解析旧的字符串项（Colo 记为空），也能解析新的对象项；写出时统一写对象。
/// </summary>
public sealed class ProxyEndpointConverter : JsonConverter<ProxyEndpoint>
{
    public override ProxyEndpoint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // 旧格式：裸字符串即 IP。
        if (reader.TokenType == JsonTokenType.String)
            return new ProxyEndpoint { Ip = reader.GetString() ?? string.Empty };

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("无法解析 PreferredEdges 条目。");

        var ep = new ProxyEndpoint();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return ep;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("PreferredEdges 对象属性名非法。");

            string? name = reader.GetString();
            reader.Read(); // 前进到该属性的值 token
            switch (name)
            {
                case nameof(ProxyEndpoint.Ip):
                    ep.Ip = reader.GetString() ?? string.Empty;
                    break;
                case nameof(ProxyEndpoint.Cdn):
                    ep.Cdn = string.IsNullOrWhiteSpace(reader.GetString()) ? "Cloudflare" : reader.GetString()!;
                    break;
                case nameof(ProxyEndpoint.Colo):
                    ep.Colo = reader.GetString() ?? string.Empty;
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("PreferredEdges 条目 JSON 不完整。");
    }

    public override void Write(Utf8JsonWriter writer, ProxyEndpoint value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(nameof(ProxyEndpoint.Ip), value.Ip);
        writer.WriteString(nameof(ProxyEndpoint.Cdn), string.IsNullOrEmpty(value.Cdn) ? "Cloudflare" : value.Cdn);
        writer.WriteString(nameof(ProxyEndpoint.Colo), value.Colo);
        writer.WriteEndObject();
    }
}
