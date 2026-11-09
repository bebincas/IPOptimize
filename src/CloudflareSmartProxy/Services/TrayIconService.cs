using System;
using System.Drawing;
using System.Windows.Forms;

namespace CloudflareSmartProxy.Services;

/// <summary>
/// 右下角托盘图标（WinForms 的 NotifyIcon——WPF 自己没有托盘控件，为这一个图标引第三方包不划算）。
/// 图标只在「缩小到托盘」期间 Visible=true，平时不占托盘位置。
/// 菜单固定四行：显示主窗口 ／ 当前优选：已启动·未启动（只读状态） ／ 关闭时缩小到托盘（记住的那个选择，可改） ／ 退出程序。
/// 事件都在 UI 线程上触发（NotifyIcon 的消息就跑在主线程的 Dispatcher 里）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _stateItem;
    private readonly ToolStripMenuItem _closeToTrayItem;
    private readonly string _title;

    /// <summary>左键点托盘图标，或点菜单里的「显示主窗口」。</summary>
    public event Action? ShowRequested;

    /// <summary>点「退出程序」：由主窗体走真正的关闭流程（停代理、关系统代理、销毁托盘）。</summary>
    public event Action? ExitRequested;

    /// <summary>在托盘菜单里勾/取消「关闭时缩小到托盘」，参数是勾选后的新状态。</summary>
    public event Action<bool>? CloseToTrayChanged;

    public TrayIconService(string title)
    {
        _title = title;

        var show = new ToolStripMenuItem("显示主窗口");
        show.Click += (_, _) => ShowRequested?.Invoke();

        _stateItem = new ToolStripMenuItem("当前优选：未启动") { Enabled = false };

        _closeToTrayItem = new ToolStripMenuItem("关闭时缩小到托盘");
        _closeToTrayItem.Click += (_, _) =>
        {
            _closeToTrayItem.Checked = !_closeToTrayItem.Checked;
            CloseToTrayChanged?.Invoke(_closeToTrayItem.Checked);
        };

        var exit = new ToolStripMenuItem("退出程序");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            show,
            new ToolStripSeparator(),
            _stateItem,
            new ToolStripSeparator(),
            _closeToTrayItem,
            exit,
        });

        _icon = new NotifyIcon
        {
            Text = title,
            Icon = LoadIcon(),
            Visible = false,
            ContextMenuStrip = menu,
        };
        // 左键点图标（单击或双击的第一下）就把主窗口叫回来。
        // 只挂 DoubleClick 不行：Win11 下图标多半躲在「显示隐藏的图标」弹层里，那个弹层会把双击的第二下吃掉，
        // 用户点了半天没反应；单击也算，双击自然更算（ShowFromTray 重复调用没有副作用）。
        _icon.MouseClick += (_, me) =>
        {
            if (me.Button == MouseButtons.Left)
                ShowRequested?.Invoke();
        };
    }

    /// <summary>图标直接用主程序自带的那个（speed.ico 已编进 exe）；取不到就退回系统应用图标。</summary>
    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon is not null)
                    return icon;
            }
        }
        catch
        {
            // 单文件发布、图标缺失都可能取不到，用系统默认图标就行，不值得为它报错。
        }
        return SystemIcons.Application;
    }

    /// <summary>刷新「当前优选是否启动」：菜单里那行 + 托盘提示文字一起改。</summary>
    public void SetRunning(bool running, string? detail = null)
    {
        var state = running ? "已启动" : "未启动";
        _stateItem.Text = "当前优选：" + state + (string.IsNullOrEmpty(detail) ? string.Empty : $"（{detail}）");
        _icon.Text = Clip($"{_title}｜{state}");
    }

    /// <summary>回填「关闭时缩小到托盘」的勾选，不触发 <see cref="CloseToTrayChanged"/>。</summary>
    public void SetCloseToTray(bool on) => _closeToTrayItem.Checked = on;

    public void Show() => _icon.Visible = true;

    public void Hide() => _icon.Visible = false;

    /// <summary>气泡提示；图标没在托盘上时什么都不做（那时用户本来就看着窗口）。</summary>
    public void Balloon(string text)
    {
        if (_icon.Visible)
            _icon.ShowBalloonTip(2500, _title, Clip(text), ToolTipIcon.Info);
    }

    /// <summary>NotifyIcon.Text 上限 63 个字符，超了 setter 直接抛 ArgumentException。</summary>
    private static string Clip(string text) => text.Length <= 63 ? text : text[..60] + "…";

    public void Dispose()
    {
        _icon.Visible = false;   // 必须先摘掉，否则进程退出后托盘会留一个死图标直到鼠标划过
        _icon.Dispose();
    }
}
