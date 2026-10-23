using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace RdpMonitorSwitcher.Views;

/// <summary>
/// Hides a notice/error banner after a while. The delay grows with the text length, restarts when the
/// text changes (e.g. download progress) and waits while the mouse is over the banner.
/// </summary>
internal sealed class BannerAutoHide
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PerCharacter = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HoverRetry = TimeSpan.FromSeconds(2);

    private readonly FrameworkElement _banner;
    private readonly Func<string?> _text;
    private readonly Action _hide;
    private readonly DispatcherTimer _timer = new();

    private BannerAutoHide(FrameworkElement banner, Func<string?> text, Action hide)
    {
        _banner = banner;
        _text = text;
        _hide = hide;
        _timer.Tick += (_, _) => OnTick();
    }

    public static void Attach(FrameworkElement banner, INotifyPropertyChanged source, string property, Func<string?> text, Action hide)
    {
        var autoHide = new BannerAutoHide(banner, text, hide);
        source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == property)
                autoHide.Restart();
        };
        autoHide.Restart();
    }

    private void Restart()
    {
        _timer.Stop();
        var text = _text();
        if (string.IsNullOrEmpty(text))
            return;
        var delay = BaseDelay + PerCharacter * text.Length;
        _timer.Interval = delay < MaxDelay ? delay : MaxDelay;
        _timer.Start();
    }

    private void OnTick()
    {
        if (_banner.IsMouseOver)
        {
            _timer.Interval = HoverRetry;
            return;
        }
        _timer.Stop();
        _hide();
    }
}
