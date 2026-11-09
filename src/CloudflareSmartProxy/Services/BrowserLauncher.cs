using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 用「系统默认浏览器」打开链接：先自己把浏览器的 exe 找出来直接起进程，认不出来才回落到 ShellExecute。
///
/// 为什么不能只靠 ShellExecute：<c>Process.Start(url, UseShellExecute=true)</c> 是把 URL 交给 Windows 去解析默认浏览器，
/// 而用户在「设置 → 默认应用」里选的那个存在 HKCU\...\Shell\Associations\UrlAssociations\http(s)\UserChoice 里
/// （带校验哈希，第三方程序不能自己写）。这一项缺失或被判无效时——没设过默认浏览器、精简镜像、组策略清理——
/// shell 退回机器级的 HKCR\http\shell\open\command，而那个键在很多系统上还指着 iexplore.exe，
/// 表现就是「点链接弹出 IE」。所以这里的次序是：UserChoice 认得出来就按它的 ProgId 定位 exe；
/// 认不出来（包括根本没设过）就从装着的浏览器里挑一个非 IE 的；
/// 只有用户明确把 IE 设成默认、或这台机器实在找不到别的浏览器，才回落到那行 ShellExecute。
///
/// 约定：<see cref="Open"/> 自己不抛异常（起进程失败就继续往下兜），调用方不必包 try/catch。
/// </summary>
public static class BrowserLauncher
{
    public static void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var exe = ResolveUserDefaultBrowser(out var ieIsDefault);
        if (exe is null && !ieIsDefault)
            exe = ResolveAnyInstalledBrowser();

        if (exe is not null && TryStart(exe, url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 拉不起来就静默：公告位只是个链接出口，不该为它弹错误框
        }
    }

    /// <summary>
    /// 参数直接交给浏览器进程，不经过 shell。
    /// 别改成 <c>cmd /c start</c>：链接里的 <c>&amp;</c> 会被 cmd 当命令分隔符截断，公告链接带查询参数正好中招。
    /// </summary>
    private static bool TryStart(string exePath, string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exePath, "\"" + url + "\"") { UseShellExecute = false });
            return true;
        }
        catch
        {
            return false;   // 路径是从注册表读的，可能对应已经卸载的浏览器；交给上层继续兜
        }
    }

    /// <summary>
    /// 按「默认应用」里的 ProgId 定位浏览器 exe。<paramref name="ieIsDefault"/> 为真＝用户确实把 IE 设成了默认，
    /// 这时不该自作主张换浏览器（调用方会直接走 ShellExecute）。
    /// </summary>
    private static string? ResolveUserDefaultBrowser(out bool ieIsDefault)
    {
        ieIsDefault = false;
        var progId = ReadUserChoiceProgId();
        if (progId is null) return null;                        // 根本没设过默认浏览器——正是会退回 IE 的那类机器
        if (IsIe(progId)) { ieIsDefault = true; return null; }

        return ExeFromAppPathsHint(progId)                      // 主流浏览器：ProgId 特征串 → exe 名 → App Paths
               ?? ExeFromProgIdCommand(progId);                 // 冷门/国产：ProgId 的打开命令里通常写着全路径
    }

    private static readonly string[] UserChoicePaths =
    {
        @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice",
        @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice",
    };

    /// <summary>读「默认应用」里选的浏览器（https 优先，其次 http）；没设过或读不到＝null。</summary>
    private static string? ReadUserChoiceProgId()
    {
        foreach (var path in UserChoicePaths)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key?.GetValue("ProgId") is string progId && progId.Length > 0) return progId;
            }
            catch { }
        }
        return null;
    }

    /// <summary>IE 自己的 ProgId（MIE / IE.HTTP 这些是 Windows 给 iexplore 的标识），不是第三方浏览器的名字。</summary>
    private static bool IsIe(string progId) =>
        progId.Equals("MIE", StringComparison.OrdinalIgnoreCase)
        || progId.Equals("IE.HTTP", StringComparison.OrdinalIgnoreCase)
        || progId.Equals("iehttp", StringComparison.OrdinalIgnoreCase)
        || progId.Contains("iexplore", StringComparison.OrdinalIgnoreCase);

    /// <summary>ProgId 特征串 → 该浏览器在 App Paths 注册的 exe 名。顺序有讲究：360chrome 之类要排在 chrome 前面。</summary>
    private static readonly (string Hint, string Exe)[] ProgIdHints =
    {
        ("360chromex", "360chromeX.exe"),
        ("360chrome", "360chrome.exe"),
        ("360se", "360se.exe"),
        ("qqbrowser", "QQBrowser.exe"),
        ("chromium", "chromium.exe"),
        ("chrome", "chrome.exe"),
        ("edge", "msedge.exe"),
        ("firefox", "firefox.exe"),
        ("brave", "brave.exe"),
        ("vivaldi", "vivaldi.exe"),
        ("opera", "opera.exe"),
    };

    private static string? ExeFromAppPathsHint(string progId)
    {
        foreach (var (hint, exeName) in ProgIdHints)
            if (progId.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                var path = ResolveFromAppPaths(exeName);
                if (path is not null) return path;
            }
        return null;
    }

    /// <summary>
    /// 冷门或国产浏览器：HKCR\&lt;ProgId&gt;\shell\open\command 里通常就写着 exe 全路径。
    /// 现代浏览器（Chrome 等）把这个键做成了 cmd.exe 转发壳、真命令放在 C 值里，所以两个都试，
    /// 而 <see cref="TryExtractExe"/> 见到 cmd.exe/rundll32 一律不要——那不是浏览器本体。
    /// </summary>
    private static string? ExeFromProgIdCommand(string progId)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            if (key is null) return null;

            foreach (var name in new[] { null, "C" })
                if (TryExtractExe(key.GetValue(name) as string, out var exe)) return exe;
        }
        catch { }
        return null;
    }

    /// <summary>HKLM 的 64 位与 32 位（WOW6432Node）两个视图 + HKCU：本程序发布为 win-x64，32 位视图写的那份不会自动合并进来。</summary>
    private static readonly (RegistryHive Hive, string Prefix)[] AppPathsRoots =
    {
        (RegistryHive.LocalMachine, @"SOFTWARE"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node"),
        (RegistryHive.CurrentUser, @"Software"),
    };

    /// <summary>按 exe 文件名在 App Paths 里找全路径（Edge/Chrome/Firefox 以及大多数国产浏览器都注册这里）。</summary>
    private static string? ResolveFromAppPaths(string exeFileName)
    {
        foreach (var (hive, prefix) in AppPathsRoots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var key = baseKey.OpenSubKey($@"{prefix}\Microsoft\Windows\CurrentVersion\App Paths\{exeFileName}");
                if (key?.GetValue(null) is string path && File.Exists(path)) return path;
            }
            catch { }
        }
        return null;
    }

    /// <summary>已知浏览器的 exe 名，按「拿不到 UserChoice 时先试谁」排序（Edge 几乎必在，故打头）。</summary>
    private static readonly string[] PreferredExes =
    {
        "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "vivaldi.exe",
        "opera.exe", "chromium.exe", "360se.exe", "360chrome.exe", "QQBrowser.exe",
    };

    /// <summary>
    /// 退而求其次（UserChoice 缺失或认不出来时）：先按名单探 App Paths，再扫 Clients\Browsers 补名单外的。
    /// App Paths 打头是因为 <c>HKLM\SOFTWARE\Clients\Browsers</c> 在 Win11 上可能整个不存在（实测这台机器就没有），
    /// 只靠它一个浏览器都挑不出来；而 App Paths 是浏览器安装器必写的键。
    /// 两条路都排除 IE：能走到这里说明确实没有默认浏览器可选，给一个现代浏览器比给 IE 有用。
    /// </summary>
    private static string? ResolveAnyInstalledBrowser()
    {
        foreach (var exeName in PreferredExes)
        {
            var path = ResolveFromAppPaths(exeName);
            if (path is not null) return path;
        }

        return ResolveFromClientsBrowsers();
    }

    /// <summary>
    /// 兜底的兜底：从 SOFTWARE\Clients\Browsers（「默认应用」设置页那份清单）里挑。
    /// 只把它当「装了哪些浏览器」的来源——该键的 (Default) 值在 Win10/11 上可能是过期值，不能当「当前默认」。
    /// </summary>
    private static string? ResolveFromClientsBrowsers()
    {
        string? best = null;
        var bestRank = int.MaxValue;

        foreach (var (hive, prefix) in BrowserListRoots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var root = baseKey.OpenSubKey($@"{prefix}\Clients\Browsers");
                if (root is null) continue;

                foreach (var name in root.GetSubKeyNames())
                {
                    if (bestRank == 0) return best;          // Edge 已经拿到，不必再翻
                    var rank = RankBrowser(name);
                    if (rank >= bestRank) continue;

                    using var browserKey = root.OpenSubKey(name);
                    if (browserKey is null) continue;

                    var exe = ExeFromBrowserKey(browserKey);
                    if (exe is null) continue;
                    best = exe;
                    bestRank = rank;
                }
            }
            catch { }
        }

        return best;
    }

    private static readonly (RegistryHive Hive, string Prefix)[] BrowserListRoots =
    {
        (RegistryHive.CurrentUser, @"Software"),
        (RegistryHive.LocalMachine, @"SOFTWARE"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node"),
    };

    private static int RankBrowser(string name) => name switch
    {
        _ when name.Contains("internet explorer", StringComparison.OrdinalIgnoreCase)
          || name.Contains("iexplore", StringComparison.OrdinalIgnoreCase)
          || name.Equals("MIE", StringComparison.OrdinalIgnoreCase) => int.MaxValue,
        _ when name.Contains("edge", StringComparison.OrdinalIgnoreCase) => 0,
        _ when name.Contains("chrome", StringComparison.OrdinalIgnoreCase) => 1,
        _ when name.Contains("firefox", StringComparison.OrdinalIgnoreCase) => 2,
        _ => 3,
    };

    /// <summary>某个浏览器注册项里的 exe：先看 shell\open\command，再看 DefaultIcon（形如 "...\chrome.exe",0）。</summary>
    private static string? ExeFromBrowserKey(RegistryKey browserKey)
    {
        foreach (var sub in new[] { @"shell\open\command", "DefaultIcon" })
        {
            try
            {
                using var key = browserKey.OpenSubKey(sub);
                if (key is not null && TryExtractExe(key.GetValue(null) as string, out var exe)) return exe;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// 从注册表命令行里取 exe：引号里的那一段就是（<c>"C:\...\chrome.exe" "%1"</c>），没有引号则取第一个空格前的词。
    /// 必须 <see cref="File.Exists"/> 才算数（注册表里的路径可能是卸载后留下的），cmd.exe/rundll32 这类转发壳直接否。
    /// </summary>
    private static bool TryExtractExe(string? command, out string? exe)
    {
        exe = null;
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (command.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || command.Contains("rundll32", StringComparison.OrdinalIgnoreCase))
            return false;

        string path;
        var start = command.IndexOf('"');
        if (start >= 0)
        {
            var end = command.IndexOf('"', start + 1);
            if (end <= start) return false;
            path = command.Substring(start + 1, end - start - 1);
        }
        else
        {
            path = command.Split(' ')[0].Trim();
        }

        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;   // 例如 start chrome 这种没有 exe 的
        if (!File.Exists(path)) return false;
        exe = path;
        return true;
    }
}
