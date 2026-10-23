using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Core.ViewModels;

public sealed partial class MonitorButtonViewModel : ObservableObject
{
    public MonitorButtonViewModel(RemoteMonitor monitor, IRelayCommand<int> select)
    {
        Monitor = monitor;
        SelectCommand = select;
    }

    public RemoteMonitor Monitor { get; }
    public int Index => Monitor.Index;
    public string Label => $"Monitor {Monitor.Number}";
    public string ToolTip => $"{Monitor.Name} – {Monitor.Resolution} (Strg+Alt+{Monitor.Number})";
    public string Resolution => Monitor.Resolution;
    public IRelayCommand<int> SelectCommand { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// One local view of the remote session (the main window or a separate monitor window).
/// Shows exactly one remote monitor at a time.
/// </summary>
public sealed partial class MonitorViewModel : ObservableObject
{
    private readonly MonitorNavigator _navigator = new();

    public MonitorViewModel(bool isMain, int initialMonitorIndex = 0)
    {
        IsMain = isMain;
        _initialIndex = initialMonitorIndex;
        _navigator.SelectionChanged += (_, _) => OnSelectionChanged();
    }

    private readonly int _initialIndex;
    private bool _initialApplied;

    /// <summary>Raised when the shown monitor or the view size changes (affects FitToWindow layouts).</summary>
    public event EventHandler? GeometryChanged;

    public event EventHandler? CloseRequested;

    public bool IsMain { get; }

    public ObservableCollection<MonitorButtonViewModel> MonitorButtons { get; } = [];

    public RemoteMonitor? SelectedMonitor => _navigator.Selected;

    public int SelectedIndex => _navigator.SelectedIndex;

    public string Title => SelectedMonitor is { } m ? $"{m.Name} – {m.Resolution}" : "Kein Monitor";

    [ObservableProperty]
    private bool _isFullscreen;

    [ObservableProperty]
    private bool _isToolbarVisible = true;

    [ObservableProperty]
    private ScalingMode _scaling = ScalingMode.Fit;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Size of the view area in device pixels (reported by the view).</summary>
    public PixelSize ViewPixelSize { get; private set; }

    public void SetMonitors(IReadOnlyList<RemoteMonitor> monitors)
    {
        _navigator.SetMonitors(monitors);
        if (!_initialApplied && monitors.Count > 0)
        {
            _initialApplied = true;
            _navigator.TrySelect(Math.Min(_initialIndex, monitors.Count - 1), out _);
        }

        MonitorButtons.Clear();
        foreach (var monitor in monitors.OrderBy(m => m.Bounds.X)) // as they sit side by side
            MonitorButtons.Add(new MonitorButtonViewModel(monitor, SelectMonitorCommand) { IsSelected = monitor.Index == SelectedIndex });

        OnPropertyChanged(nameof(SelectedMonitor));
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasMultipleMonitors));
    }

    public bool HasMultipleMonitors => _navigator.Monitors.Count > 1;

    public void ReportViewSize(PixelSize size)
    {
        if (size == ViewPixelSize)
            return;
        ViewPixelSize = size;
        GeometryChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SelectMonitor(int index)
    {
        if (_navigator.TrySelect(index, out var error))
            ErrorMessage = null;
        else
            ErrorMessage = error;
    }

    [RelayCommand]
    private void NextMonitor()
    {
        ErrorMessage = null;
        _navigator.Next();
    }

    [RelayCommand]
    private void PreviousMonitor()
    {
        ErrorMessage = null;
        _navigator.Previous();
    }

    [RelayCommand]
    private void ToggleFullscreen() => IsFullscreen = !IsFullscreen;

    [RelayCommand]
    private void ToggleToolbar() => IsToolbarVisible = !IsToolbarVisible;

    [RelayCommand]
    private void ToggleScaling() => Scaling = Scaling == ScalingMode.Fit ? ScalingMode.Original : ScalingMode.Fit;

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnSelectionChanged()
    {
        foreach (var button in MonitorButtons)
            button.IsSelected = button.Index == SelectedIndex;
        OnPropertyChanged(nameof(SelectedMonitor));
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(Title));
        GeometryChanged?.Invoke(this, EventArgs.Empty);
    }
}
