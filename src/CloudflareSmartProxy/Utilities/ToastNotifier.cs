using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CloudflareSmartProxy.Utilities;

/// <summary>
/// 轻量 Toast / Snackbar：往窗口角落的 StackPanel 里追加一张卡片，滑入淡显、停留后淡出自移除。
/// 纯代码动画，不依赖第三方控件库；超出保留条数时立即丢弃最旧的一条，防止连续操作堆满屏幕。
/// </summary>
public static class ToastNotifier
{
    /// <summary>提示色调，对应左侧竖条颜色。</summary>
    public enum Kind
    {
        Info,
        Success,
        Warn,
        Error,
    }

    /// <summary>同时最多保留的条数。</summary>
    private const int MaxToasts = 4;

    private static Color AccentColor(Kind kind) => kind switch
    {
        Kind.Success => Colors.Green,
        Kind.Warn => Colors.DarkOrange,
        Kind.Error => Colors.Red,
        _ => Colors.Gray,
    };

    /// <summary>
    /// 弹出一条 Toast。host 必须是 XAML 里声明的角宿 StackPanel（见 MainWindow 的 ToastHost），需在 UI 线程调用。
    /// </summary>
    /// <param name="holdMs">完全显示的停留时长（毫秒），到点后淡出并从容器移除。</param>
    public static void Show(StackPanel host, string message, Kind kind = Kind.Info, int holdMs = 2600)
    {
        while (host.Children.Count >= MaxToasts)
            host.Children.RemoveAt(0); // 挤掉最旧的一条

        var slide = new TranslateTransform(0, 14);
        var card = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0,
            RenderTransform = slide,
            Child = new Grid(),
        };

        var grid = (Grid)card.Child;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var accent = new Border
        {
            Width = 4,
            Background = new SolidColorBrush(AccentColor(kind)),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Grid.SetColumn(accent, 0);
        grid.Children.Add(accent);

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Foreground = Brushes.Black,
            Margin = new Thickness(12, 9, 14, 9),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        host.Children.Add(card);

        // 一条 Storyboard 排完整个生命周期：滑入淡显 → 停留 → 淡出；Completed 时自我清理。
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        Storyboard.SetTarget(fadeIn, card);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));

        var slideIn = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slideIn, card);
        Storyboard.SetTargetProperty(slideIn, new PropertyPath("RenderTransform.Y"));

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(420))
        {
            BeginTime = TimeSpan.FromMilliseconds(Math.Max(400, holdMs)),
        };
        Storyboard.SetTarget(fadeOut, card);
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));

        var sb = new Storyboard();
        sb.Children.Add(fadeIn);
        sb.Children.Add(slideIn);
        sb.Children.Add(fadeOut);
        sb.Completed += (_, _) => host.Children.Remove(card);
        sb.Begin();
    }
}
