using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;

namespace CloudflareSmartProxy.Services
{
    /// <summary>
    /// Internet 代理管理。
    /// 启动代理时把系统代理指向本机端口并写入固定的本地绕过列表；
    /// 停止/退出时只关闭「我们自己设的那一份」，不备份也不还原用户原有的代理配置。
    /// </summary>
    internal static class SystemProxy
    {
        private const string InternetSettings =
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        /// <summary>
        /// 启动代理时写入的绕过列表：本机与常用内网段直连，不走代理。
        /// 这个字符串同时是「这份系统代理是我们设的」的识别标记，改动它会让旧版本写下的配置认不出来。
        /// </summary>
        public const string LocalBypass =
            "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.2*.*;172.30.*;172.31.*;192.168.*;<local>";

        /// <summary>本进程写进注册表的代理值；关闭时据此判断当前配置是不是自己设的。</summary>
        private static string? _writtenProxy;

        [DllImport(
            "wininet.dll",
            SetLastError = true)]
        private static extern bool InternetSetOption(
            IntPtr hInternet,
            int dwOption,
            IntPtr lpBuffer,
            int dwBufferLength);

        /// <summary>
        /// 把系统代理指向指定地址：ProxyEnable=1、ProxyServer=proxy、ProxyOverride=<see cref="LocalBypass"/>。
        /// 不读取也不保存原值。
        /// </summary>
        /// <param name="proxy">例如：127.0.0.1:7890</param>
        public static void SetProxy(string proxy)
        {
            if (string.IsNullOrWhiteSpace(proxy))
                throw new ArgumentException(
                    "代理地址不能为空。",
                    nameof(proxy));

            using RegistryKey key = OpenWritableKey();

            key.SetValue("ProxyServer", proxy.Trim(), RegistryValueKind.String);
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyOverride", LocalBypass, RegistryValueKind.String);
            _writtenProxy = proxy.Trim();
            Refresh();
        }

        /// <summary>
        /// 关闭本程序设的系统代理：ProxyEnable=0，并删除我们写入的 ProxyServer / ProxyOverride。
        /// 当前系统代理不是我们设的（用户自己的代理、或运行期间被手动改过）时什么都不做，
        /// 避免误伤同样占用 127.0.0.1 的其它工具。重复调用是幂等的。
        /// </summary>
        /// <param name="message">给用户看的处理结果。</param>
        /// <returns>是否真的改动了注册表。</returns>
        public static bool ClearProxy(out string message)
        {
            message = string.Empty;

            using RegistryKey key = OpenWritableKey();

            string? server = Convert.ToString(key.GetValue("ProxyServer"), System.Globalization.CultureInfo.InvariantCulture)?.Trim();
            string? bypass = Convert.ToString(key.GetValue("ProxyOverride"), System.Globalization.CultureInfo.InvariantCulture)?.Trim();
            bool enabled = TryToInt(key.GetValue("ProxyEnable")) != 0;

            bool ours;
            if (_writtenProxy is not null)
            {
                // 本进程写过：只认自己写的那个值，运行期间被用户手动改过就当他是有意的。
                ours = string.Equals(server, _writtenProxy, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // 没有写入记录（上次被强杀、这次只是收尾）：认我们独有的绕过列表 + 环回地址。
                ours = enabled
                    && string.Equals(bypass, LocalBypass, StringComparison.OrdinalIgnoreCase)
                    && server is not null
                    && (server.StartsWith("127.0.0.1:", StringComparison.OrdinalIgnoreCase)
                        || server.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase));
            }

            if (!ours)
            {
                if (_writtenProxy is not null)
                    message = $"系统代理已被手动改为「{server ?? "(空)"}」，保留你的设置，未改动。";
                else if (string.IsNullOrEmpty(server))
                    message = "系统代理未指向本程序，注册表未改动。";
                else
                    message = $"当前系统代理为「{server}」，不是本程序设置的，保持原样未改动。";
                return false;
            }

            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
            _writtenProxy = null;
            Refresh();

            message = "已关闭系统代理（ProxyEnable=0，并清除本程序写入的 ProxyServer / ProxyOverride）。";
            return true;
        }

        /// <summary>获取当前代理是否启用。</summary>
        public static bool IsEnabled()
        {
            using RegistryKey? key =
                Registry.CurrentUser.OpenSubKey(
                    InternetSettings,
                    writable: false);

            if (key == null)
                return false;

            object? value =
                key.GetValue("ProxyEnable");

            return value != null &&
                   Convert.ToInt32(value) != 0;
        }

        /// <summary>获取当前代理服务器。</summary>
        public static string? GetProxy()
        {
            using RegistryKey? key =
                Registry.CurrentUser.OpenSubKey(
                    InternetSettings,
                    writable: false);

            if (key == null)
                return null;

            return key.GetValue(
                "ProxyServer") as string;
        }

        /// <summary>获取当前代理绕过规则。</summary>
        public static string? GetProxyOverride()
        {
            using RegistryKey? key =
                Registry.CurrentUser.OpenSubKey(
                    InternetSettings,
                    writable: false);

            if (key == null)
                return null;

            return key.GetValue(
                "ProxyOverride") as string;
        }

        // ------------------------------------------------------------------ 内部实现

        private static RegistryKey OpenWritableKey() =>
            Registry.CurrentUser.OpenSubKey(InternetSettings, writable: true)
            ?? throw new InvalidOperationException("无法打开 Internet Settings 注册表项。");

        private static int TryToInt(object? raw)
        {
            try
            {
                return raw is null ? 0 : Convert.ToInt32(raw);
            }
            catch
            {
                return 0;
            }
        }

        private static void Refresh()
        {
            InternetSetOption(
                IntPtr.Zero,
                INTERNET_OPTION_SETTINGS_CHANGED,
                IntPtr.Zero,
                0);

            InternetSetOption(
                IntPtr.Zero,
                INTERNET_OPTION_REFRESH,
                IntPtr.Zero,
                0);
        }
    }
}
