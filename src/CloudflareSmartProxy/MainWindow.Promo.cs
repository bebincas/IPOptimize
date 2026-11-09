using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CloudflareSmartProxy.Services;

namespace CloudflareSmartProxy;

/// <summary>
/// 顶部公告位：开窗时拉一次公告 JSON（图片地址 + 跳转链接），图片显示在标题右边那条 60 高的槽位里
/// （<c>PromoBanner</c> 这个 Border + 里面的 <c>PromoImage</c>），点击用系统浏览器打开链接
/// （<see cref="BrowserLauncher"/>：先认默认浏览器的 exe，认不出来才退回 ShellExecute）。
///
/// 只取一次：不轮询、不重试、不定时刷新，settings.json 里也没有对应字段和开关。
///
/// 与之前那个 WebBrowser 方案的实质差别是**这边能判成败**：任何一步失败（拉不到、JSON 非法、字段缺、
/// 图片过大、解码失败）就把整块 Collapsed，不占标题栏那 60px；原因只写文件日志（<see cref="AppLog"/>，
/// DEBUG 级），不弹框也不升 WARN——公告位不值得打断用户，UI 日志那边本来也只显示 INFO 以上。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 公告地址，返回的形状是 <c>{"image":"https://.../banner.png","link":"https://..."}</c>，
    /// 多余的字段反序列化时忽略（字段名对不上就同时改 <see cref="PromoPayload"/>）。
    /// 现在指向 <c>promo.php</c>：它输出的确实是那行 JSON，但响应头还是 <c>Content-Type: text/html</c>——
    /// 这里不看 header、直接按 JSON 解，所以没影响；别「顺手」加个 content-type 判断，那样反而会被服务端绊住。
    /// </summary>
    private const string PromoApiUrl = "https://gh-go.com/promo/promo.php";

    /// <summary>
    /// 给个像样的 User-Agent：某些站点（尤其挂着 CDN/防护的）对完全没有 UA 的请求直接 403，
    /// 而原来那个 WebBrowser 是带着 IE 的 UA 去取的，换 HttpClient 后这层差别得自己补上。
    /// </summary>
    private const string PromoUserAgent = "IPOptimize/1.0";

    /// <summary>单次请求时限（JSON 一次、图片一次各算）：它在 Loaded 里后台跑，不能把开窗后的 UI 拖住。</summary>
    private static readonly TimeSpan PromoTimeout = TimeSpan.FromSeconds(10);

    /// <summary>图片字节上限（5MB）：公告 banner 不该是几 MB，超了宁可不要这张，别拿解码时间和内存去换。</summary>
    private const long PromoMaxImageBytes = 5 * 1024 * 1024;

    /// <summary>点 banner 要打开的链接。没取到公告时一直是 null（banner 也收着，根本点不到）。</summary>
    private string? _promoLink;

    /// <summary>公告 JSON 的形状：只用 image / link 两项。（类型名别和 XAML 的 x:Name 撞——那会生成同名字段。）</summary>
    private sealed record PromoPayload(
        [property: JsonPropertyName("image")] string? Image,
        [property: JsonPropertyName("link")] string? Link);

    /// <summary>Loaded 里调一次：后台拉公告，不阻塞开窗。</summary>
    private void InitPromo()
    {
        _ = ShowPromoAsync();
    }

    /// <summary>
    /// 拉 JSON → 下载图片 → 解码 → 显示。
    /// 全程**不写** <c>ConfigureAwait(false)</c>：唯一的调用来自 Loaded（UI 线程），
    /// 所以每个 await 之后都回到 UI 线程，收尾可以直接碰 PromoBanner / PromoImage，不必绕 Dispatcher。
    /// </summary>
    private async Task ShowPromoAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = PromoTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(PromoUserAgent);

            var json = await http.GetStringAsync(PromoApiUrl);
            var promo = JsonSerializer.Deserialize<PromoPayload>(json);
            if (promo is null || string.IsNullOrWhiteSpace(promo.Image) || string.IsNullOrWhiteSpace(promo.Link))
            {
                HidePromo("公告 JSON 里没有可用的 image/link");
                return;
            }

            if (!Uri.TryCreate(promo.Image, UriKind.Absolute, out var imageUri)
                || (imageUri.Scheme != Uri.UriSchemeHttp && imageUri.Scheme != Uri.UriSchemeHttps))
            {
                HidePromo($"公告图片地址非法: {promo.Image}");
                return;
            }

            var bytes = await http.GetByteArrayAsync(imageUri);
            if (bytes.Length == 0 || bytes.Length > PromoMaxImageBytes)
            {
                HidePromo($"公告图片大小不合适：{bytes.Length} 字节（上限 {PromoMaxImageBytes / 1024 / 1024}MB）");
                return;
            }

            var source = DecodePromoImage(bytes);
            if (source is null)
            {
                HidePromo($"公告图片解码失败（WPF 不认这种格式）: {imageUri}");
                return;
            }

            _promoLink = promo.Link;
            PromoImage.Source = source;
            PromoBanner.ToolTip = promo.Link;              // 悬停能看到点下去去哪儿
            PromoBanner.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // HttpClient 的超时也是 OperationCanceledException 家族（内层 TimeoutException），
            // 和断网、5xx、JSON 非法一样按「这次没有公告」处理：收起、留一句文件日志，别弹框。
            HidePromo($"公告加载失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 从内存字节解码成 BitmapSource 并 Freeze。
    /// <see cref="BitmapCacheOption.OnLoad"/> 是硬要求：BitmapImage 不内建像素就会一直挂着你给的流，
    /// 流出作用域被 Dispose 之后，图片在下一次重绘时静默变空白（用 File.Open 喂流时尤其容易踩）。
    /// </summary>
    private static BitmapSource? DecodePromoImage(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"公告图片解码异常：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>收起公告位（不占位）并留一句原因到文件日志。</summary>
    private void HidePromo(string reason)
    {
        _promoLink = null;
        PromoImage.Source = null;
        PromoBanner.Visibility = Visibility.Collapsed;
        AppLog.Debug($"[公告] {reason}");
    }

    /// <summary>
    /// 点公告图＝用浏览器打开链接。链接为空（还没拉到或这次拉失败）就什么都不做。
    /// <see cref="BrowserLauncher.Open"/> 自己不抛异常，这里不需要包 try/catch。
    /// </summary>
    private void PromoBanner_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_promoLink)) return;
        BrowserLauncher.Open(_promoLink);
    }

    /// <summary>
    /// 点右上角叉号＝把公告收起来，**只管本次运行**：不写 settings.json，下次开窗照常再取再显示（这是选定行为，
    /// 别顺手加持久化把它永久关掉）。
    /// <c>e.Handled = true</c> 是必须的：外层 <c>PromoBanner</c> 那个 Border 的 MouseLeftButtonUp 是「打开链接」，
    /// 路由事件会从叉号一路冒上去，不拦住的话点叉号会连带把浏览器也开起来。
    /// </summary>
    private void PromoClose_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HidePromo("用户点了叉号，本次运行不再显示公告（下次开窗还会取）");
    }
}
