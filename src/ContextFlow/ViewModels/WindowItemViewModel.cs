using System.Windows.Media;
using ContextFlow.Infrastructure;
using ContextFlow.Models;

namespace ContextFlow.ViewModels;

public sealed class WindowItemViewModel : ObservableObject
{
    private string _title = string.Empty;
    private string _processName = string.Empty;
    private ImageSource? _icon;
    private string _capsuleId = string.Empty;
    private string _capsuleName = string.Empty;
    private string _capsuleAccent = "#6366F1";
    private Brush _capsuleBrush = ColorUtil.BrushFromHex("#6366F1");
    private Brush _capsuleSoftBrush = ColorUtil.WithAlpha("#6366F1", 36);
    private string _monitorLabel = string.Empty;
    private string _coordinatesLabel = string.Empty;
    private string _sizeLabel = string.Empty;
    private bool _isForeground;
    private bool _isMinimized;
    private bool _isCloaked;
    private ScreenRect _bounds;

    private WindowItemViewModel(ManagedWindow window)
    {
        Handle = window.Handle;
        ProcessId = window.ProcessId;
        FirstSeenUtc = window.FirstSeenUtc;
        Update(window, null, null);
    }

    public IntPtr Handle { get; }
    public int ProcessId { get; }
    public DateTime FirstSeenUtc { get; }
    public string Initial => string.IsNullOrWhiteSpace(_processName) ? "?" : _processName[..1].ToUpperInvariant();
    public string ProcessLabel => $"{_processName}  ·  PID {ProcessId}";
    public string HandleLabel => $"0x{Handle.ToInt64():X}";
    public bool HasIcon => _icon is not null;

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string ProcessName
    {
        get => _processName;
        private set
        {
            if (SetProperty(ref _processName, value))
            {
                OnPropertyChanged(nameof(Initial));
                OnPropertyChanged(nameof(ProcessLabel));
            }
        }
    }

    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public string CapsuleId
    {
        get => _capsuleId;
        private set => SetProperty(ref _capsuleId, value);
    }

    public string CapsuleName
    {
        get => _capsuleName;
        private set => SetProperty(ref _capsuleName, value);
    }

    public string CapsuleAccent
    {
        get => _capsuleAccent;
        private set => SetProperty(ref _capsuleAccent, value);
    }

    public Brush CapsuleBrush
    {
        get => _capsuleBrush;
        private set => SetProperty(ref _capsuleBrush, value);
    }

    public Brush CapsuleSoftBrush
    {
        get => _capsuleSoftBrush;
        private set => SetProperty(ref _capsuleSoftBrush, value);
    }

    public string MonitorLabel
    {
        get => _monitorLabel;
        private set => SetProperty(ref _monitorLabel, value);
    }

    public string CoordinatesLabel
    {
        get => _coordinatesLabel;
        private set => SetProperty(ref _coordinatesLabel, value);
    }

    public string SizeLabel
    {
        get => _sizeLabel;
        private set => SetProperty(ref _sizeLabel, value);
    }

    public string BoundsToolTip => $"{_bounds.Width} × {_bounds.Height} at {_bounds.X}, {_bounds.Y}";

    public bool IsForeground
    {
        get => _isForeground;
        private set
        {
            if (SetProperty(ref _isForeground, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public bool IsMinimized
    {
        get => _isMinimized;
        private set
        {
            if (SetProperty(ref _isMinimized, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public bool IsCloaked
    {
        get => _isCloaked;
        private set
        {
            if (SetProperty(ref _isCloaked, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public ScreenRect Bounds => _bounds;

    public string StatusLabel => IsForeground
        ? "FOCUSED"
        : IsMinimized
            ? "MINIMIZED"
            : IsCloaked
                ? "CLOAKED"
                : "ON SCREEN";

    public static WindowItemViewModel Create(ManagedWindow window) => new(window);

    public void Update(ManagedWindow window, CapsuleItemViewModel? capsule, DisplayInfo? display)
    {
        Title = string.IsNullOrWhiteSpace(window.Title) ? "(untitled window)" : window.Title;
        ProcessName = window.ProcessName;
        Icon = window.Icon;
        CapsuleId = window.CapsuleId;
        IsForeground = window.IsForeground;
        IsMinimized = window.IsMinimized;
        IsCloaked = window.IsCloaked;
        _bounds = window.Bounds;
        CoordinatesLabel = window.CoordinatesLabel;
        SizeLabel = $"{window.Bounds.Width}×{window.Bounds.Height}";
        MonitorLabel = display?.Label ?? "Display —";
        OnPropertyChanged(nameof(BoundsToolTip));

        if (capsule is not null)
        {
            CapsuleName = capsule.Name;
            CapsuleAccent = capsule.Accent;
            CapsuleBrush = capsule.AccentBrush;
            CapsuleSoftBrush = capsule.AccentSoftBrush;
        }

        if (display is not null)
        {
            MonitorLabel = $"{display.Label} · {display.ResolutionLabel}";
        }
    }
}
