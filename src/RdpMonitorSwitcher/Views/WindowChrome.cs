using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RdpMonitorSwitcher.Core.ViewModels;

namespace RdpMonitorSwitcher.Views;

/// <summary>
/// Fullscreen handling and the auto-hiding toolbar shared by the main window and the
/// monitor windows. The toolbar sits above the remote view normally; in fullscreen (or when
/// hidden with Ctrl+Alt+T) it slides in as an overlay when the mouse touches the top edge.
/// </summary>
internal sealed class WindowChrome
{
    private readonly Window _window;
    private readonly MonitorViewModel _view;
    private readonly FrameworkElement _toolbar;
    private readonly FrameworkElement _hotZone;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResize;
    private bool _isFullscreen;
    private bool _peeking;

    public WindowChrome(Window window, MonitorViewModel view, FrameworkElement toolbar, FrameworkElement hotZone)
    {
        _window = window;
        _view = view;
        _toolbar = toolbar;
        _hotZone = hotZone;
        _view.PropertyChanged += OnViewPropertyChanged;
        _hotZone.MouseEnter += (_, _) => Peek(true);
        _toolbar.MouseLeave += (_, _) => Peek(false);
        Update();
    }

    private bool ToolbarDocked => !_view.IsFullscreen && _view.IsToolbarVisible;

    private void OnViewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MonitorViewModel.IsFullscreen) or nameof(MonitorViewModel.IsToolbarVisible))
            Update();
    }

    private void Update()
    {
        if (_view.IsFullscreen != _isFullscreen)
            SetFullscreen(_view.IsFullscreen);

        _peeking = false;
        if (ToolbarDocked)
        {
            Grid.SetRow(_toolbar, 0);
            _toolbar.VerticalAlignment = VerticalAlignment.Stretch;
            _toolbar.Visibility = Visibility.Visible;
            _hotZone.Visibility = Visibility.Collapsed;
        }
        else
        {
            Grid.SetRow(_toolbar, 1);
            _toolbar.VerticalAlignment = VerticalAlignment.Top;
            _toolbar.Visibility = Visibility.Collapsed;
            _hotZone.Visibility = Visibility.Visible;
        }
    }

    private void Peek(bool show)
    {
        if (ToolbarDocked || show == _peeking)
            return;
        if (!show && _toolbar.IsKeyboardFocusWithin)
            return;
        _peeking = show;
        _toolbar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetFullscreen(bool fullscreen)
    {
        _isFullscreen = fullscreen;
        if (fullscreen)
        {
            _restoreState = _window.WindowState;
            _restoreStyle = _window.WindowStyle;
            _restoreResize = _window.ResizeMode;
            // Normal first, otherwise a maximized window keeps the taskbar visible.
            _window.WindowState = WindowState.Normal;
            _window.WindowStyle = WindowStyle.None;
            _window.ResizeMode = ResizeMode.NoResize;
            _window.WindowState = WindowState.Maximized;
        }
        else
        {
            _window.WindowStyle = _restoreStyle;
            _window.ResizeMode = _restoreResize;
            _window.WindowState = _restoreState;
        }
    }
}
