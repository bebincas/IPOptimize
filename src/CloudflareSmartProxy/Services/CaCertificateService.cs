using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// MITM 根 CA：首次运行生成并持久化（%LOCALAPPDATA%\CloudflareSmartProxy\rootCA.pfx），
/// 之后按域名动态签发叶子证书（含 SAN，父域通配符兜底）。
/// 注意：本类只在「设置」里启用 DNS 加速解密（MITM）时才会被使用；未启用时代理只做透明隧道，不签发任何证书。
/// </summary>
public sealed class CaCertificateService
{
    public const string CaSubject = "CN=CloudflareSmartProxy Root CA";
    private readonly string _pfxPath;
    private readonly X509Certificate2 _root;
    private readonly ConcurrentDictionary<string, X509Certificate2> _domainCerts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次运行是否新生成了根证书（false＝从磁盘加载的既有根）。</summary>
    public bool CreatedNew { get; }
    public X509Certificate2 Root => _root;
    public string PfxPath => _pfxPath;

    public CaCertificateService(string storageDirectory)
    {
        Directory.CreateDirectory(storageDirectory);
        _pfxPath = Path.Combine(storageDirectory, "rootCA.pfx");

        if (File.Exists(_pfxPath))
        {
            try
            {
                _root = new X509Certificate2(_pfxPath, (string?)null,
                    X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.MachineKeySet);
                if (_root.GetRSAPrivateKey() is null)
                    throw new InvalidOperationException("根证书缺少私钥");
                CreatedNew = false;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"加载根证书失败，重新生成: {ex.Message}");
                _root = CreateAndSaveRoot();
                CreatedNew = true;
            }
        }
        else
        {
            _root = CreateAndSaveRoot();
            CreatedNew = true;
        }
    }

    private X509Certificate2 CreateAndSaveRoot()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(CaSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var root = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(10));
        try
        {
            File.WriteAllBytes(_pfxPath, root.Export(X509ContentType.Pfx));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"根证书持久化失败（不影响本次运行）: {ex.Message}");
        }
        AppLog.Info($"已生成 MITM 根证书: {CaSubject} (指纹 {root.Thumbprint[..8]}…)");
        return root;
    }

    /// <summary>导出根证书公钥（.cer），供 Firefox 等独立证书库手动导入。</summary>
    public void ExportPublicCertificate(string path) =>
        File.WriteAllBytes(path, Root.Export(X509ContentType.Cert));

    /// <summary>获取（或签发）指定域名的叶子证书。并发安全，结果缓存整个进程周期。</summary>
    public X509Certificate2 GetOrCreateDomainCertificate(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return _domainCerts.GetOrAdd(host, static (h, root) => IssueDomainCertificate(h, root), _root);
    }

    private static X509Certificate2 IssueDomainCertificate(string host, X509Certificate2 root)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // SAN：域名本身 + 父域通配符（如 raw.githubusercontent.com → *.githubusercontent.com）
        var names = new System.Collections.Generic.List<string> { host };
        var firstDot = host.IndexOf('.');
        if (firstDot > 0 && firstDot < host.Length - 1)
            names.Add("*." + host[(firstDot + 1)..]);
        req.CertificateExtensions.Add(CertificateSanBuilder.BuildSanExtension(names));

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") /* serverAuth */ }, false));

        var serial = RandomNumberGenerator.GetBytes(16);
        var notBefore = DateTimeOffset.UtcNow.AddHours(-2);
        var notAfter = DateTimeOffset.UtcNow.AddDays(825); // Windows/浏览器友好上限

        var cert = req.Create(root, notBefore, notAfter, serial);
        using var withKey = cert.CopyWithPrivateKey(rsa);

        // Windows SChannel 不支持纯内存(ephemeral)私钥：导出 PFX 后重新加载，
        // 让私钥进入可用的 key 容器，否则 SslStream 服务端握手会报
        // "the platform does not support ephemeral keys"。
        var pfx = withKey.Export(X509ContentType.Pfx);
        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.DefaultKeySet);
    }

    public int CachedDomainCertCount => _domainCerts.Count;
}
