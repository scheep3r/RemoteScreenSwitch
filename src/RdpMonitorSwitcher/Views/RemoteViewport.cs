using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Rdp;
using RdpMonitorSwitcher.Core.Services;
using RdpMonitorSwitcher.Core.ViewModels;
using RdpMonitorSwitcher.Infrastructure;
using RdpMonitorSwitcher.Services;

namespace RdpMonitorSwitcher.Views;

/// <summary>
/// Displays one remote monitor (a rectangle of the shared frame buffer) and forwards mouse
/// input to the session. Keyboard input is routed by <see cref="InputRouter"/> while this
/// control has the keyboard focus.
/// </summary>
public sealed class RemoteViewport : Grid
{
    private readonly ScrollViewer _scroll;
    private readonly Image _image;
    private readonly Border _dragZone;
    private readonly Border _dropHint;
    private DragOutTrigger _dragOut = new(new DragOutSettings());
    private bool _inputSuspended;

    private AppServices? _services;
    private MonitorViewModel? _view;
    private WriteableBitmap? _bitmap;
    private uint _generation = uint.MaxValue;
    private bool _needsFullCopy = true;
    private ushort _pressedButtons;
    private ushort _pressedExtendedButtons;
    private PixelPoint _lastPosition = new(-1, -1);
    private HwndSource? _hwndSource;

    public RemoteViewport()
    {
        Background = Brushes.Black;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _image = new Image
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
        };
        _scroll = new ScrollViewer
        {
            Content = _image,
            Focusable = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Children.Add(_scroll);

        _dragZone = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x70, 0x0A, 0x6C, 0xD8)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xD0, 0x1F, 0x7F, 0xE8)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "Dateien → lokaler PC",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Children.Add(_dragZone);

        _dropHint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x50, 0x0A, 0x6C, 0xD8)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0x1F, 0x7F, 0xE8)),
            BorderThickness = new Thickness(3),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x26, 0x26, 0x26)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 8, 14, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 24, 0, 0),
                Child = new TextBlock
                {
                    Text = "Loslassen = an dieser Stelle auf dem Remote-PC einfügen",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                },
            },
        };
        Children.Add(_dropHint);
        AllowDrop = true;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => ReportSize();
    }

    public MonitorViewModel? View => _view;

    public Window? OwnerWindow { get; private set; }

    /// <summary>A drag inside this view reached the drag-out zone.</summary>
    internal event EventHandler? DragOutTriggered;

    /// <summary>Local files were dropped on this view at the given remote desktop point.</summary>
    internal event EventHandler<(IReadOnlyList<string> Paths, PixelPoint Point)>? FilesDropped;

    /// <summary>The left button is held in this view (as far as the remote session knows).</summary>
    internal bool IsLeftButtonHeld => (_pressedButtons & MouseFlags.Button1) != 0;

    /// <summary>Connects the control to the app services and the view model of its view.</summary>
    internal void Attach(AppServices services, MonitorViewModel view)
    {
        _services = services;
        _view = view;
        _view.PropertyChanged += OnViewPropertyChanged;
        ApplyScalingMode();
        ReportSize();
        Cursor = services.Cursors.Current;
        services.Cursors.Changed += OnRemoteCursorChanged;
        ApplyDragOutSettings();
        services.Main.DragOutSettingsChanged += OnDragOutSettingsChanged;
    }

    private void OnDragOutSettingsChanged(object? sender, EventArgs e) => ApplyDragOutSettings();

    private void ApplyDragOutSettings()
    {
        if (_services is null)
            return;
        var settings = _services.Main.Settings.DragOut.Normalized();
        _dragOut = new DragOutTrigger(settings);
        var thickness = Math.Max(settings.Size, 6);
        var vertical = settings.Edge is ScreenEdge.Left or ScreenEdge.Right;
        _dragZone.Width = vertical ? thickness : double.NaN;
        _dragZone.Height = vertical ? double.NaN : thickness;
        _dragZone.HorizontalAlignment = settings.Edge switch
        {
            ScreenEdge.Left => HorizontalAlignment.Left,
            ScreenEdge.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Stretch,
        };
        _dragZone.VerticalAlignment = settings.Edge switch
        {
            ScreenEdge.Top => VerticalAlignment.Top,
            ScreenEdge.Bottom => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Stretch,
        };
        _dragZone.BorderThickness = settings.Edge switch
        {
            ScreenEdge.Left => new Thickness(0, 0, 2, 0),
            ScreenEdge.Right => new Thickness(2, 0, 0, 0),
            ScreenEdge.Top => new Thickness(0, 0, 0, 2),
            _ => new Thickness(0, 2, 0, 0),
        };
        var label = (TextBlock)_dragZone.Child;
        label.LayoutTransform = vertical ? new RotateTransform(settings.Edge == ScreenEdge.Left ? -90 : 90) : Transform.Identity;
        label.Visibility = thickness >= 18 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IRdpSession? Session =>
        _services?.Main.IsConnected == true ? _services.Main.Controller.Session : null;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        OwnerWindow = Window.GetWindow(this);
        _services?.Renderer.Register(this);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            _hwndSource = source;
            _hwndSource.AddHook(WndProc);
        }
        ReportSize();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ReleaseMouseButtons();
        _services?.Renderer.Unregister(this);
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    internal void Detach()
    {
        if (_view is not null)
            _view.PropertyChanged -= OnViewPropertyChanged;
        if (_services is not null)
        {
            _services.Cursors.Changed -= OnRemoteCursorChanged;
            _services.Main.DragOutSettingsChanged -= OnDragOutSettingsChanged;
            _services.Renderer.Unregister(this);
        }
        _view = null;
    }

    private void OnViewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MonitorViewModel.SelectedMonitor):
                ReleaseMouseButtons();
                _needsFullCopy = true;
                _services?.Renderer.RequestRefresh();
                break;
            case nameof(MonitorViewModel.Scaling):
                ApplyScalingMode();
                break;
        }
    }

    private void OnRemoteCursorChanged(object? sender, EventArgs e) => Cursor = _services?.Cursors.Current;

    private void ApplyScalingMode()
    {
        var original = _view?.Scaling == ScalingMode.Original;
        _image.Stretch = original ? Stretch.None : Stretch.Uniform;
        _scroll.HorizontalScrollBarVisibility = original ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollBarVisibility = original ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        UpdateScalingQuality();
    }

    private void ReportSize()
    {
        if (_view is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        _view.ReportViewSize(ViewportMapper.FitSize(ActualWidth, ActualHeight, dpi.DpiScaleX));
        UpdateScalingQuality();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _bitmap = null; // recreated with the new DPI
        _needsFullCopy = true;
        ReportSize();
        _services?.Renderer.RequestRefresh();
    }

    /// <summary>Use nearest-neighbour when drawing 1:1 so text stays sharp, smooth scaling otherwise.</summary>
    private void UpdateScalingQuality()
    {
        if (_view?.SelectedMonitor is not { } monitor || ActualWidth <= 0)
        {
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var rect = ViewportMapper.ComputeImageRect(monitor.Bounds.Size, ActualWidth, ActualHeight,
            _view.Scaling, dpi.DpiScaleX);
        var scale = rect.Width * dpi.DpiScaleX / monitor.Bounds.Width;
        RenderOptions.SetBitmapScalingMode(_image,
            Math.Abs(scale - 1) < 0.01 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    // ---- rendering -----------------------------------------------------------------

    /// <summary>Copies the changed part of this view's monitor from the locked frame buffer.</summary>
    internal unsafe void UpdateFrom(in FrameData frame)
    {
        if (_view?.SelectedMonitor is not { } monitor)
            return;

        var size = monitor.Bounds.Size;
        if (_bitmap is null || _bitmap.PixelWidth != size.Width || _bitmap.PixelHeight != size.Height)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _bitmap = new WriteableBitmap(size.Width, size.Height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgr32, null);
            _image.Source = _bitmap;
            _needsFullCopy = true;
            UpdateScalingQuality();
        }
        if (frame.Generation != _generation)
        {
            _generation = frame.Generation;
            _needsFullCopy = true;
        }

        var frameRect = new PixelRect(0, 0, frame.Width, frame.Height);
        var source = (_needsFullCopy ? monitor.Bounds : frame.Dirty.Intersect(monitor.Bounds)).Intersect(frameRect);
        _needsFullCopy = false;
        if (source.IsEmpty)
            return;

        _bitmap.Lock();
        try
        {
            var dst = (byte*)_bitmap.BackBuffer;
            var dstStride = _bitmap.BackBufferStride;
            var src = (byte*)frame.Data;
            var rowBytes = source.Width * 4;
            var dx = source.X - monitor.Bounds.X;
            var dy = source.Y - monitor.Bounds.Y;
            for (var row = 0; row < source.Height; row++)
            {
                Buffer.MemoryCopy(
                    src + (long)(source.Y + row) * frame.Stride + source.X * 4L,
                    dst + (long)(dy + row) * dstStride + dx * 4L,
                    rowBytes, rowBytes);
            }
            _bitmap.AddDirtyRect(new Int32Rect(dx, dy, source.Width, source.Height));
        }
        finally
        {
            _bitmap.Unlock();
        }
    }

    /// <summary>Called when the session ended: show black instead of a stale image.</summary>
    internal void Clear()
    {
        _bitmap = null;
        _image.Source = null;
        _needsFullCopy = true;
    }

    // ---- mouse -----------------------------------------------------------------------

    private bool TryMap(MouseEventArgs e, out PixelPoint point) => TryMap(e.GetPosition(_image), out point);

    private bool TryMap(Point position, out PixelPoint point)
    {
        point = default;
        if (_view?.SelectedMonitor is not { } monitor || _image.ActualWidth <= 0)
            return false;
        point = ViewportMapper.ToDesktop(monitor, position.X, position.Y, _image.ActualWidth, _image.ActualHeight);
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_inputSuspended || Session is not { } session || !TryMap(e, out var p))
            return;
        if (p != _lastPosition)
        {
            _lastPosition = p;
            session.SendMouse(MouseFlags.Move, p.X, p.Y);
        }
        UpdateDragOut(e.GetPosition(this));
    }

    private void UpdateDragOut(Point position)
    {
        if (!IsLeftButtonHeld || _services?.Main.CanPullFiles != true)
        {
            _dragZone.Visibility = Visibility.Collapsed;
            return;
        }
        var now = Environment.TickCount64;
        if (_dragOut.Move(now, position.X, position.Y, ActualWidth, ActualHeight))
        {
            _dragZone.Visibility = Visibility.Collapsed;
            DragOutTriggered?.Invoke(this, EventArgs.Empty);
            return;
        }
        _dragZone.Visibility = _dragOut.IsArmed(now) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Ends a drag on the remote side (Esc, then button up) and ignores the mouse until all
    /// buttons are released locally; used when the drag continues on the local PC.
    /// </summary>
    internal void CancelRemoteDrag()
    {
        if (Session is { } session && IsLeftButtonHeld)
        {
            const uint escape = 0x01;
            session.SendKey(true, escape);
            session.SendKey(false, escape);
        }
        ReleaseMouseButtons();
        _inputSuspended = true;
        _dragZone.Visibility = Visibility.Collapsed;
    }

    internal void ResumeInput() => _inputSuspended = false;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Keyboard.Focus(this);
        if (_inputSuspended || Session is not { } session || !TryMap(e, out var p))
            return;
        CaptureMouse();
        SendButton(session, e.ChangedButton, down: true, p);
        if (e.ChangedButton == MouseButton.Left)
        {
            var position = e.GetPosition(this);
            _dragOut.ButtonDown(Environment.TickCount64, position.X, position.Y);
        }
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Left)
        {
            _dragOut.ButtonUp();
            _dragZone.Visibility = Visibility.Collapsed;
        }
        if (_inputSuspended)
        {
            e.Handled = true;
            return;
        }
        if (Session is { } session && TryMap(e, out var p))
            SendButton(session, e.ChangedButton, down: false, p);
        if (_pressedButtons == 0 && _pressedExtendedButtons == 0)
            ReleaseMouseCapture();
        e.Handled = true;
    }

    // Preview (tunnelling) on purpose: the inner ScrollViewer marks every wheel event as handled,
    // so the bubbling OnMouseWheel would never be called. The wheel always belongs to the remote PC.
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (_inputSuspended || Session is not { } session || !TryMap(e, out var p))
            return;
        foreach (var flags in MouseFlags.WheelSteps(e.Delta, horizontal: false))
            session.SendMouse(flags, p.X, p.Y);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        ReleaseMouseButtons();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEHWHEEL && IsMouseOver && Session is { } session &&
            TryMap(Mouse.GetPosition(_image), out var p))
        {
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            foreach (var flags in MouseFlags.WheelSteps(delta, horizontal: true))
                session.SendMouse(flags, p.X, p.Y);
            handled = true;
        }
        return 0;
    }

    private void SendButton(IRdpSession session, MouseButton button, bool down, PixelPoint p)
    {
        switch (button)
        {
            case MouseButton.Left:
            case MouseButton.Right:
            case MouseButton.Middle:
            {
                var flag = button switch
                {
                    MouseButton.Left => MouseFlags.Button1,
                    MouseButton.Right => MouseFlags.Button2,
                    _ => MouseFlags.Button3,
                };
                _pressedButtons = down ? (ushort)(_pressedButtons | flag) : (ushort)(_pressedButtons & ~flag);
                session.SendMouse((ushort)(flag | (down ? MouseFlags.Down : 0)), p.X, p.Y);
                break;
            }
            case MouseButton.XButton1:
            case MouseButton.XButton2:
            {
                var flag = button == MouseButton.XButton1 ? MouseFlags.ExtendedButton1 : MouseFlags.ExtendedButton2;
                _pressedExtendedButtons = down ? (ushort)(_pressedExtendedButtons | flag) : (ushort)(_pressedExtendedButtons & ~flag);
                session.SendExtendedMouse((ushort)(flag | (down ? MouseFlags.ExtendedDown : 0)), p.X, p.Y);
                break;
            }
        }
    }

    /// <summary>Sends button-up for every pressed mouse button (e.g. before switching monitors).</summary>
    internal void ReleaseMouseButtons()
    {
        var session = Session;
        if (session is not null && (_pressedButtons != 0 || _pressedExtendedButtons != 0))
        {
            var p = _lastPosition.X >= 0 ? _lastPosition : default;
            foreach (var flag in new[] { MouseFlags.Button1, MouseFlags.Button2, MouseFlags.Button3 })
            {
                if ((_pressedButtons & flag) != 0)
                    session.SendMouse(flag, p.X, p.Y);
            }
            foreach (var flag in new[] { MouseFlags.ExtendedButton1, MouseFlags.ExtendedButton2 })
            {
                if ((_pressedExtendedButtons & flag) != 0)
                    session.SendExtendedMouse(flag, p.X, p.Y);
            }
        }
        _pressedButtons = 0;
        _pressedExtendedButtons = 0;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    // ---- dropping local files ----------------------------------------------------------

    private bool AcceptsDrop(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && _services?.FileTransfer?.CanAcceptDrop == true;

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        UpdateDrop(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        UpdateDrop(e);
    }

    private void UpdateDrop(DragEventArgs e)
    {
        var accept = AcceptsDrop(e);
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        _dropHint.Visibility = accept ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        _dropHint.Visibility = Visibility.Collapsed;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        _dropHint.Visibility = Visibility.Collapsed;
        if (!AcceptsDrop(e) || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths ||
            !TryMap(e.GetPosition(_image), out var point))
            return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        FilesDropped?.Invoke(this, (paths, point));
    }

    // ---- keyboard focus -----------------------------------------------------------------

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _services?.Input.Activate(this);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _services?.Input.Deactivate(this);
    }
}
