using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 手工 DER 编码 subjectAltName (2.5.29.17) 扩展：
/// SEQUENCE OF GeneralName，dNSName 为 [2] IA5String（0x82 标签）。
/// </summary>
public static class CertificateSanBuilder
{
    public static X509Extension BuildSanExtension(IEnumerable<string> dnsNames)
    {
        using var names = new MemoryStream();
        foreach (var name in dnsNames)
        {
            var bytes = Encoding.ASCII.GetBytes(name);
            if (bytes.Length == 0 || bytes.Length > 255)
                throw new ArgumentException($"非法 SAN 名称: {name}", nameof(dnsNames));
            names.WriteByte(0x82);                    // GeneralName dNSName (context tag 2, primitive)
            names.WriteByte((byte)bytes.Length);
            names.Write(bytes, 0, bytes.Length);
        }
        var generalNames = names.ToArray();

        using var seq = new MemoryStream();
        seq.WriteByte(0x30);                          // SEQUENCE
        WriteDerLength(seq, generalNames.Length);
        seq.Write(generalNames, 0, generalNames.Length);

        return new X509Extension("2.5.29.17", seq.ToArray(), critical: false);
    }

    private static void WriteDerLength(MemoryStream ms, int length)
    {
        if (length < 128)
        {
            ms.WriteByte((byte)length);
        }
        else if (length < 256)
        {
            ms.WriteByte(0x81);
            ms.WriteByte((byte)length);
        }
        else
        {
            ms.WriteByte(0x82);
            ms.WriteByte((byte)(length >> 8));
            ms.WriteByte((byte)(length & 0xFF));
        }
    }
}
