using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 根证书信任安装/卸载。
/// - CurrentUser\Root：无需管理员，Windows 会弹一次安全确认对话框（Chrome/Edge 走系统信任链）。
/// - LocalMachine\Root：需管理员，不弹窗，信任所有用户（本程序以 asInvoker 运行，走提权自我重启）。
/// </summary>
public static class CertificateInstaller
{
    public const string InstallCaArgument = "--install-ca";
    public const string UninstallCaArgument = "--uninstall-ca";

    public static bool IsInstalled(X509Certificate2 cert, StoreLocation location)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false);
            return found.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前用户或本机任一处已信任即视为已安装。</summary>
    public static bool IsInstalledAnywhere(X509Certificate2 cert) =>
        IsInstalled(cert, StoreLocation.CurrentUser) || IsInstalled(cert, StoreLocation.LocalMachine);

    public static bool TryInstall(X509Certificate2 cert, StoreLocation location)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadWrite);
            store.Add(cert);
            AppLog.Info($"根证书已安装到 {location}\\Root (指纹 {cert.Thumbprint[..8]}…)");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"安装根证书到 {location} 失败: {ex.Message}");
            return false;
        }
    }

    public static bool TryUninstall(X509Certificate2 cert, StoreLocation location)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadWrite);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false);
            foreach (var c in found) store.Remove(c);
            AppLog.Info($"根证书已从 {location}\\Root 移除");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"从 {location} 移除根证书失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>卸载所有位置的本工具根证书（凭主题名定位，允许 pfx 已丢失的场景）。</summary>
    public static int TryUninstallBySubject(string subject)
    {
        var removed = 0;
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadWrite);
                var found = store.Certificates.Find(X509FindType.FindBySubjectName, subject, validOnly: false);
                foreach (var c in found)
                {
                    store.Remove(c);
                    removed++;
                    AppLog.Info($"已从 {location}\\Root 移除 {subject}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"清理 {location}\\Root 失败: {ex.Message}");
            }
        }
        return removed;
    }
}
