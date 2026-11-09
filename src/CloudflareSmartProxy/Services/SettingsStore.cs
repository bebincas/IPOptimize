using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CloudflareSmartProxy.Models;

namespace CloudflareSmartProxy.Services;

public sealed class SettingsStore
{
    // 程序数据目录（settings.json 与日志、根证书同目录）。
    internal static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CloudflareSmartProxy");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    // IncludeFields=true：AppSettings 的测速选项是公有字段，不加该选项 System.Text.Json 会跳过它们，导致重启后恢复默认值。
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, IncludeFields = true };

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();
            await using var stream = File.OpenRead(FilePath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions) ?? new AppSettings();
            // 内置 GitHub 白名单始终保留：老配置里是空的、或用户把默认条目删过，读回来时并回去。
            settings.GlobalDnsWhitelist = DnsWhitelistDefaults.Merge(settings.GlobalDnsWhitelist);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        await using var stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
    }

    /// <summary>
    /// 同步保存。关窗时「记住我的选择」必须用这个：那时进程可能马上就要退出，
    /// 异步写还没落盘就被掐掉，下次启动又是「第一次」，等于没记住。
    /// </summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
