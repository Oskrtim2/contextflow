using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ContextFlow.Infrastructure;
using ContextFlow.Models;
using ContextFlow.Services;

namespace ContextFlow.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly string[] CapsulePalette =
    [
        "#6366F1", "#14B8A6", "#F59E0B", "#EC4899", "#0EA5E9", "#A855F7"
    ];

    private readonly SettingsService _settingsService;
    private readonly WindowManager _windowManager;
    private readonly DisplayService _displayService;
    private readonly WindowNavigator _navigator;
    private readonly HardwareAIOptimizer _optimizer;
    private readonly TelemetryService _telemetry;
    private readonly LocalizationService _localization;
    private readonly AppLog _log;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<IntPtr, WindowItemViewModel> _windowLookup = new();
    private readonly List<double> _cpuHistory = new();
    private readonly List<double> _ramHistory = new();
    private readonly List<double> _gpuHistory = new();

    private CapsuleItemViewModel? _activeCapsule;
    private ActivityItemViewModel? _latestActivity;
    private string _searchText = string.Empty;
    private bool _showAllWindows = true;
    private bool _isDarkTheme;
    private bool _isOptimizationEnabled;
    private bool _isIsolationEnabled;
    private bool _isDisposed;

    private double _cpuPercent;
    private double _ramPercent;
    private double _gpuPercent;
    private double _ramUsedGb;
    private double _ramTotalGb;
    private string _gpuDetail = LocalizationService.Get("WaitingForPdh");
    private string _systemStatusText = "Nominal";
    private string _lastTelemetryLabel = "—";
    private IReadOnlyList<double> _cpuHistoryView = Array.Empty<double>();
    private IReadOnlyList<double> _ramHistoryView = Array.Empty<double>();
    private IReadOnlyList<double> _gpuHistoryView = Array.Empty<double>();
    private int _windowCount;
    private int _activeCapsuleWindowCount;
    private int _visibleWindowCount;
    private string _hotkeyStatusLabel = "INITIALIZING";
    private string _hotkeyDetail = LocalizationService.Get("HotkeyDetail");
    private bool _hotkeyReady;
    private WindowNavigationResult? _currentNavigation;
    private string _currentWindowTitle = LocalizationService.Get("NoWindowSelected");
    private string _currentWindowProcess = LocalizationService.Get("ScrollHint");
    private string _windowScrollPositionText = "—";
    private bool _hasScrollWindow;
    private bool _isWorkspaceTab = true;
    private bool _isCapturingHotkey;
    private bool _captureScrollUp;
    private string _settingsStatusMessage = string.Empty;
    private bool _hasSettingsStatus;

    public MainViewModel(
        SettingsService settingsService,
        WindowManager windowManager,
        DisplayService displayService,
        WindowNavigator navigator,
        HardwareAIOptimizer optimizer,
        TelemetryService telemetry,
        LocalizationService localization,
        AppLog log)
    {
        _settingsService = settingsService;
        _windowManager = windowManager;
        _displayService = displayService;
        _navigator = navigator;
        _optimizer = optimizer;
        _telemetry = telemetry;
        _localization = localization;
        _log = log;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _isDarkTheme = _settingsService.Current.IsDarkTheme;
        _isOptimizationEnabled = _settingsService.Current.AutoOptimize;
        _isIsolationEnabled = _settingsService.Current.CapsuleIsolation;

        _displayService.Refresh();
        Displays = new ObservableCollection<DisplayItemViewModel>(
            _displayService.Current.Select(d => new DisplayItemViewModel(d)));

        Capsules = new ObservableCollection<CapsuleItemViewModel>(
            _settingsService.Current.Capsules.Select((c, index) =>
                new CapsuleItemViewModel(c, ResolveDisplayLabel(c.TargetDisplay), index + 1)));

        _activeCapsule = Capsules.FirstOrDefault(c =>
                               string.Equals(c.Id, _settingsService.Current.ActiveCapsuleId, StringComparison.Ordinal))
                           ?? Capsules.FirstOrDefault();
        if (_activeCapsule is not null)
        {
            _settingsService.Current.ActiveCapsuleId = _activeCapsule.Id;
            UpdateActiveFlags();
        }

        Windows = new ObservableCollection<WindowItemViewModel>();
        WindowsView = CollectionViewSource.GetDefaultView(Windows);
        WindowsView.Filter = FilterWindow;

        Activity = new ObservableCollection<ActivityItemViewModel>();
        Commands = new MainCommands(this);

        _windowManager.WindowsChanged += OnWindowsChanged;
        _telemetry.SampleReceived += OnTelemetrySample;
        _optimizer.ActivityReported += OnOptimizerActivity;
        _navigator.Navigated += OnWindowNavigated;
        _localization.LanguageChanged += OnLanguageChanged;

        _windowManager.ConfigureCapsules(
            _settingsService.Current.Capsules,
            _activeCapsule?.Id ?? string.Empty);

        AddActivity(new OptimizationEvent(
            DateTimeOffset.Now,
            "ContextFlow",
            OptimizationEventKind.PolicyStarted,
            LocalizationService.Format("PolicyOnlineFormat", _optimizer.EngineLabel),
            true));

        if (_telemetry.LastSample.Timestamp != default)
        {
            ApplyTelemetry(_telemetry.LastSample);
        }

        ApplyWindowSnapshot(_windowManager.Snapshot);
        ApplyNavigation(_navigator.ResolveCurrent());
    }

    public ObservableCollection<CapsuleItemViewModel> Capsules { get; }
    public ObservableCollection<DisplayItemViewModel> Displays { get; }
    public ObservableCollection<WindowItemViewModel> Windows { get; }
    public ObservableCollection<ActivityItemViewModel> Activity { get; }
    public ICollectionView WindowsView { get; }
    public MainCommands Commands { get; }

    public event Action<bool>? ThemeChangeRequested;
    public event EventHandler? CollapseRequested;
    public event EventHandler? ExitRequested;
    public event Action<int>? WindowNavigationRequested;
    public event EventHandler? HotkeysChanged;
    public event Action<bool>? HotkeyCaptureStateChanged;

    /// <summary>
    /// Raised after activating a capsule restores minimized windows so the
    /// controller can return keyboard focus to the topmost HUD.
    /// </summary>
    public event EventHandler? HudFocusRequested;

    public CapsuleItemViewModel? ActiveCapsule => _activeCapsule;

    public PageLayout PageLayout
    {
        get => _settingsService.Current.PageLayout;
        set
        {
            if (_settingsService.Current.PageLayout == value)
            {
                return;
            }

            _settingsService.Current.PageLayout = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLayoutAuto));
            OnPropertyChanged(nameof(IsLayoutColumns));
            OnPropertyChanged(nameof(IsLayoutRows));
            OnPropertyChanged(nameof(IsLayoutGrid));
            if (ActiveCapsule is not null)
            {
                ActivateCapsule(ActiveCapsule);
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshFilter();
            }
        }
    }

    public bool ShowAllWindows
    {
        get => _showAllWindows;
        set
        {
            if (!SetProperty(ref _showAllWindows, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowCapsuleOnly));
            OnPropertyChanged(nameof(IsAllFilterSelected));
            OnPropertyChanged(nameof(IsCapsuleFilterSelected));
            RefreshFilter();
        }
    }

    public bool ShowCapsuleOnly => !_showAllWindows;

    public bool IsAllFilterSelected
    {
        get => _showAllWindows;
        set
        {
            if (value)
            {
                ShowAllWindows = true;
            }
        }
    }

    public bool IsCapsuleFilterSelected
    {
        get => !_showAllWindows;
        set
        {
            if (value)
            {
                ShowAllWindows = false;
            }
        }
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (!SetProperty(ref _isDarkTheme, value))
            {
                return;
            }

            _settingsService.Current.IsDarkTheme = value;
            SaveSettings();
            OnPropertyChanged(nameof(ThemeIconGlyph));
            ThemeChangeRequested?.Invoke(value);
        }
    }

    public string ThemeIconGlyph => IsDarkTheme ? "☾" : "☀";

    public bool IsOptimizationEnabled
    {
        get => _isOptimizationEnabled;
        set
        {
            if (!SetProperty(ref _isOptimizationEnabled, value))
            {
                return;
            }

            _optimizer.IsEnabled = value;
            _settingsService.Current.AutoOptimize = value;
            SaveSettings();
            OnPropertyChanged(nameof(AiStatusText));
            OnPropertyChanged(nameof(AiStatusDetail));
            Commands.OptimizeNow.RaiseCanExecuteChanged();

            AddActivity(new OptimizationEvent(
                DateTimeOffset.Now,
                "ContextFlow",
                value ? OptimizationEventKind.PolicyStarted : OptimizationEventKind.PolicyStopped,
                LocalizationService.Get(value ? "OptimizationResumed" : "OptimizationPaused"),
                true));
        }
    }

    public bool IsCapsuleIsolationEnabled
    {
        get => _isIsolationEnabled;
        set
        {
            if (!SetProperty(ref _isIsolationEnabled, value))
            {
                return;
            }

            _optimizer.SetCompositionCloaking(value);
            _settingsService.Current.CapsuleIsolation = value;
            SaveSettings();
            OnPropertyChanged(nameof(IsolationStatusText));
        }
    }

    public string AiStatusText => LocalizationService.Get(
        IsOptimizationEnabled ? "AiActive" : "AiPausedStatus");

    public string AiStatusDetail => LocalizationService.Get(
        IsOptimizationEnabled ? "AiWatching" : "AiPaused");

    public string IsolationStatusText => LocalizationService.Get(
        IsCapsuleIsolationEnabled ? "IsolationOn" : "IsolationOff");

    public bool HotkeyReady
    {
        get => _hotkeyReady;
        private set => SetProperty(ref _hotkeyReady, value);
    }

    public string CurrentWindowTitle
    {
        get => _currentWindowTitle;
        private set => SetProperty(ref _currentWindowTitle, value);
    }

    public string CurrentWindowProcess
    {
        get => _currentWindowProcess;
        private set => SetProperty(ref _currentWindowProcess, value);
    }

    public string WindowScrollPositionText
    {
        get => _windowScrollPositionText;
        private set => SetProperty(ref _windowScrollPositionText, value);
    }

    public bool HasScrollWindow
    {
        get => _hasScrollWindow;
        private set => SetProperty(ref _hasScrollWindow, value);
    }

    public bool IsWorkspaceTab
    {
        get => _isWorkspaceTab;
        set
        {
            if (SetProperty(ref _isWorkspaceTab, value))
            {
                if (value)
                {
                    CancelHotkeyCapture();
                }

                OnPropertyChanged(nameof(IsSettingsTab));
            }
        }
    }

    public bool IsSettingsTab
    {
        get => !_isWorkspaceTab;
        set
        {
            if (value)
            {
                IsWorkspaceTab = false;
            }
        }
    }

    public bool IsCapturingHotkey
    {
        get => _isCapturingHotkey;
        private set
        {
            if (SetProperty(ref _isCapturingHotkey, value))
            {
                OnPropertyChanged(nameof(ScrollUpButtonText));
                OnPropertyChanged(nameof(ScrollDownButtonText));
            }
        }
    }

    public string ScrollUpHotkeyDisplay =>
        HotkeyGesture.Parse(_settingsService.Current.ScrollUpHotkey, HotkeyGesture.DefaultScrollUp).Display;

    public string ScrollDownHotkeyDisplay =>
        HotkeyGesture.Parse(_settingsService.Current.ScrollDownHotkey, HotkeyGesture.DefaultScrollDown).Display;

    public string ScrollShortcutHint => $"{ScrollUpHotkeyDisplay} ▲ / {ScrollDownHotkeyDisplay} ▼";
    public string PreviousHotkeyTooltip =>
        LocalizationService.Format("TooltipPreviousFormat", ScrollUpHotkeyDisplay);
    public string NextHotkeyTooltip =>
        LocalizationService.Format("TooltipNextFormat", ScrollDownHotkeyDisplay);
    public string HudShortcutLabel => "Ctrl+E+F";
    public string HudShortcutBadge => $"{HudShortcutLabel} HUD";
    public string ScrollUpButtonText => IsCapturingHotkey && _captureScrollUp
        ? LocalizationService.Get("PressKeys")
        : LocalizationService.Get("Change");
    public string ScrollDownButtonText => IsCapturingHotkey && !_captureScrollUp
        ? LocalizationService.Get("PressKeys")
        : LocalizationService.Get("Change");

    public string SettingsStatusMessage
    {
        get => _settingsStatusMessage;
        private set => SetProperty(ref _settingsStatusMessage, value);
    }

    public bool HasSettingsStatus
    {
        get => _hasSettingsStatus;
        private set => SetProperty(ref _hasSettingsStatus, value);
    }

    public bool IsEnglish
    {
        get => _localization.Language != LocalizationService.Spanish;
        set
        {
            if (value)
            {
                SetLanguage(LocalizationService.English);
            }
        }
    }

    public bool IsSpanish
    {
        get => _localization.Language == LocalizationService.Spanish;
        set
        {
            if (value)
            {
                SetLanguage(LocalizationService.Spanish);
            }
        }
    }

    public bool IsLayoutAuto
    {
        get => PageLayout == PageLayout.Auto;
        set { if (value) PageLayout = PageLayout.Auto; }
    }

    public bool IsLayoutColumns
    {
        get => PageLayout == PageLayout.Columns;
        set { if (value) PageLayout = PageLayout.Columns; }
    }

    public bool IsLayoutRows
    {
        get => PageLayout == PageLayout.Rows;
        set { if (value) PageLayout = PageLayout.Rows; }
    }

    public bool IsLayoutGrid
    {
        get => PageLayout == PageLayout.Grid;
        set { if (value) PageLayout = PageLayout.Grid; }
    }

    public string HotkeyStatusLabel
    {
        get => _hotkeyStatusLabel;
        private set => SetProperty(ref _hotkeyStatusLabel, value);
    }

    public string HotkeyDetail
    {
        get => _hotkeyDetail;
        private set => SetProperty(ref _hotkeyDetail, value);
    }

    public double CpuPercent
    {
        get => _cpuPercent;
        private set
        {
            if (SetProperty(ref _cpuPercent, value))
            {
                OnPropertyChanged(nameof(CpuValueText));
            }
        }
    }

    public double RamPercent
    {
        get => _ramPercent;
        private set
        {
            if (SetProperty(ref _ramPercent, value))
            {
                OnPropertyChanged(nameof(RamValueText));
            }
        }
    }

    public double GpuPercent
    {
        get => _gpuPercent;
        private set
        {
            if (SetProperty(ref _gpuPercent, value))
            {
                OnPropertyChanged(nameof(GpuValueText));
            }
        }
    }

    public string CpuValueText => $"{CpuPercent:0}";
    public string RamValueText => $"{RamPercent:0}";
    public string GpuValueText => GpuDetail == "Unavailable" ? "—" : $"{GpuPercent:0}";

    public string CpuDetail => LocalizationService.Format("CpuDetailFormat", Environment.ProcessorCount);
    public string RamDetail => _ramTotalGb > 0
        ? $"{_ramUsedGb:0.0} / {_ramTotalGb:0.0} GB"
        : LocalizationService.Get("ReadingMemory");

    public string GpuDetail
    {
        get => _gpuDetail;
        private set
        {
            if (SetProperty(ref _gpuDetail, value))
            {
                OnPropertyChanged(nameof(GpuValueText));
                OnPropertyChanged(nameof(GpuIsAvailable));
            }
        }
    }

    public bool GpuIsAvailable => !string.Equals(GpuDetail, "Unavailable", StringComparison.Ordinal);

    public IReadOnlyList<double> CpuHistory
    {
        get => _cpuHistoryView;
        private set => SetProperty(ref _cpuHistoryView, value);
    }

    public IReadOnlyList<double> RamHistory
    {
        get => _ramHistoryView;
        private set => SetProperty(ref _ramHistoryView, value);
    }

    public IReadOnlyList<double> GpuHistory
    {
        get => _gpuHistoryView;
        private set => SetProperty(ref _gpuHistoryView, value);
    }

    public string SystemStatusText
    {
        get => _systemStatusText;
        private set
        {
            if (SetProperty(ref _systemStatusText, value))
            {
                OnPropertyChanged(nameof(SystemStatusLabel));
            }
        }
    }

    public string SystemStatusLabel => LocalizationService.Format("SystemFormat", SystemStatusText);

    public string LastTelemetryLabel
    {
        get => _lastTelemetryLabel;
        private set
        {
            if (SetProperty(ref _lastTelemetryLabel, value))
            {
                OnPropertyChanged(nameof(TelemetryLabel));
            }
        }
    }

    public string TelemetryLabel => LocalizationService.Format("TelemetryFormat", LastTelemetryLabel);

    public int WindowCount
    {
        get => _windowCount;
        private set
        {
            if (SetProperty(ref _windowCount, value))
            {
                OnPropertyChanged(nameof(WindowCountText));
                OnPropertyChanged(nameof(BubbleCountText));
                OnPropertyChanged(nameof(WindowSummaryText));
            }
        }
    }

    public string WindowCountText => WindowCount.ToString("00");
    public string BubbleCountText => WindowCount > 99 ? "99+" : WindowCount.ToString();

    public string WindowSummaryText => LocalizationService.Format(
        "WindowSummaryFormat",
        WindowCount,
        Capsules.Count);

    public int ActiveCapsuleWindowCount
    {
        get => _activeCapsuleWindowCount;
        private set
        {
            if (SetProperty(ref _activeCapsuleWindowCount, value))
            {
                OnPropertyChanged(nameof(ActiveCapsuleCountText));
            }
        }
    }

    public string ActiveCapsuleCountText => ActiveCapsuleWindowCount == 1
        ? LocalizationService.Get("WindowCountOne")
        : LocalizationService.Format("ActivePageCountFormat", ActiveCapsuleWindowCount);

    public int VisibleWindowCount
    {
        get => _visibleWindowCount;
        private set
        {
            if (SetProperty(ref _visibleWindowCount, value))
            {
                OnPropertyChanged(nameof(VisibleWindowCountText));
                OnPropertyChanged(nameof(HasVisibleWindows));
                OnPropertyChanged(nameof(HasNoVisibleWindows));
            }
        }
    }

    public string VisibleWindowCountText => VisibleWindowCount == 1
        ? LocalizationService.Get("WindowCountOne")
        : LocalizationService.Format("WindowCountFormat", VisibleWindowCount);
    public bool HasVisibleWindows => VisibleWindowCount > 0;
    public bool HasNoVisibleWindows => VisibleWindowCount == 0;

    public string ActiveCapsuleName => ActiveCapsule?.Name ?? LocalizationService.Get("NoPage");
    public string ActiveCapsuleDisplayLabel => ActiveCapsule?.TargetDisplayLabel ?? "—";

    public string CapsuleSummary => Capsules.Count == 1
        ? LocalizationService.Get("PageCountOne")
        : LocalizationService.Format("PageCountFormat", Capsules.Count);

    public string DisplaySummary => Displays.Count == 1
        ? LocalizationService.Get("DisplayCountOne")
        : LocalizationService.Format("DisplayCountFormat", Displays.Count);

    public int ActiveDisplayIndex
    {
        get
        {
            if (ActiveCapsule is null)
            {
                return 0;
            }

            var index = Displays
                .Select((display, i) => (display, i))
                .FirstOrDefault(pair => string.Equals(
                    pair.display.DeviceName,
                    ActiveCapsule.Model.TargetDisplay,
                    StringComparison.OrdinalIgnoreCase));
            return index.display is null ? 0 : index.i;
        }
        set
        {
            if (ActiveCapsule is null || value < 0 || value >= Displays.Count)
            {
                return;
            }

            var display = Displays[value].Display;
            if (string.Equals(ActiveCapsule.Model.TargetDisplay, display.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ActiveCapsule.Model.TargetDisplay = display.DeviceName;
            ActiveCapsule.SetTargetDisplayLabel($"{display.Label} · {display.ResolutionLabel}");
            _settingsService.Current.Capsules
                .FirstOrDefault(c => c.Id == ActiveCapsule.Id)!
                .TargetDisplay = display.DeviceName;

            var result = _windowManager.ArrangeCapsuleWindows(
                ActiveCapsule.Id,
                display,
                _settingsService.Current.PageLayout,
                focusFirst: false);
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveCapsuleDisplayLabel));

            if (result.Moved > 0)
            {
                AddActivity(new OptimizationEvent(
                    DateTimeOffset.Now,
                    ActiveCapsule.Name,
                    OptimizationEventKind.CapsuleRouted,
                    LocalizationService.Format(
                        "RoutedWindowsFormat",
                        result.Moved,
                        $"{display.Label} · {display.ResolutionLabel}"),
                    true));
                QueueCapture();
            }
        }
    }

    public string LastOptimizationText => _latestActivity?.Detail ?? LocalizationService.Get("AwaitingPolicyDecision");

    public string LastOptimizationProcess => _latestActivity?.ProcessName ?? LocalizationService.Get("LocalPolicyLabel");

    public string LastOptimizationTime => _latestActivity?.TimeLabel ?? "—";

    public string SessionStartLabel => DateTime.Now.ToString("ddd, dd MMM · HH:mm");

    public string BuildLabel => "v1.0.0 · local-first";

    public string EngineLabel => _optimizer.EngineLabel;

    public bool IsOnnxAvailable => _optimizer.IsOnnxAvailable;

    public void SetHotkeyStatus(bool ready, string? error = null)
    {
        HotkeyReady = ready;
        HotkeyStatusLabel = LocalizationService.Get(ready ? "HotkeysReady" : "HotkeysUnavailable");
        HotkeyDetail = ready
            ? LocalizationService.Get("HotkeyDetail")
            : error ?? "Another application may be capturing keyboard input.";
        OnPropertyChanged(nameof(HotkeyReady));
    }

    public void ActivateCapsule(CapsuleItemViewModel capsule, bool focusFirst = false)
    {
        if (_isDisposed)
        {
            return;
        }

        var changed = !ReferenceEquals(capsule, _activeCapsule);
        if (changed)
        {
            _activeCapsule = capsule;
            _settingsService.Current.ActiveCapsuleId = capsule.Id;
            UpdateActiveFlags();
            OnPropertyChanged(nameof(ActiveCapsule));
            OnPropertyChanged(nameof(ActiveCapsuleName));
            OnPropertyChanged(nameof(ActiveCapsuleDisplayLabel));
            OnPropertyChanged(nameof(ActiveCapsuleCountText));
            OnPropertyChanged(nameof(ActiveDisplayIndex));

            _windowManager.SetActiveCapsule(capsule.Id);
            _optimizer.NotifyActiveCapsuleChanged(capsule.Id);
        }

        var target = ResolveDisplay(capsule.Model.TargetDisplay);
        if (target is not null)
        {
            var move = _windowManager.ArrangeCapsuleWindows(
                capsule.Id,
                target,
                _settingsService.Current.PageLayout,
                focusFirst);
            if (move.Moved > 0)
            {
                _log.Info($"Activated page '{capsule.Name}' and tiled {move.Moved} window(s) on {target.Label}.");
                QueueCapture();
            }
        }

        if (changed)
        {
            SaveSettings();
        }

        RefreshFilter();
        OnPropertyChanged(nameof(ActiveCapsuleDisplayLabel));

        if (!focusFirst)
        {
            HudFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void GoToPage(CapsuleItemViewModel capsule)
    {
        ActivateCapsule(capsule, focusFirst: true);
        CollapseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void FocusWindow(WindowItemViewModel window)
    {
        var capsule = Capsules.FirstOrDefault(c => c.Id == window.CapsuleId);
        if (capsule is not null && !ReferenceEquals(capsule, _activeCapsule))
        {
            ActivateCapsule(capsule);
        }

        var navigation = _navigator.FocusWindow(window.Handle);
        if (navigation is not null)
        {
            ApplyNavigation(navigation);
        }

        CollapseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void SetLanguage(string language)
    {
        if (string.Equals(_localization.Language, language, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _localization.Apply(language);
        _settingsService.Current.Language = _localization.Language;
        SaveSettings();
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsSpanish));
        OnPropertyChanged(nameof(ScrollUpButtonText));
        OnPropertyChanged(nameof(ScrollDownButtonText));
    }

    public void BeginHotkeyCapture(bool scrollUp)
    {
        _captureScrollUp = scrollUp;
        IsCapturingHotkey = true;
        HasSettingsStatus = false;
        SettingsStatusMessage = string.Empty;
        HotkeyCaptureStateChanged?.Invoke(true);
        OnPropertyChanged(nameof(ScrollUpButtonText));
        OnPropertyChanged(nameof(ScrollDownButtonText));
    }

    public void CancelHotkeyCapture()
    {
        if (!IsCapturingHotkey)
        {
            return;
        }

        IsCapturingHotkey = false;
        HotkeyCaptureStateChanged?.Invoke(false);
        OnPropertyChanged(nameof(ScrollUpButtonText));
        OnPropertyChanged(nameof(ScrollDownButtonText));
    }

    public bool TryCaptureHotkey(Key key, ModifierKeys modifiers, bool windows = false)
    {
        if (!IsCapturingHotkey)
        {
            return false;
        }

        var gesture = HotkeyGesture.From(key, modifiers, windows);
        if (!gesture.IsValid)
        {
            SetSettingsStatus(LocalizationService.Get("NoModifierWarning"));
            return true;
        }

        var currentUp = HotkeyGesture.Parse(
            _settingsService.Current.ScrollUpHotkey,
            HotkeyGesture.DefaultScrollUp);
        var currentDown = HotkeyGesture.Parse(
            _settingsService.Current.ScrollDownHotkey,
            HotkeyGesture.DefaultScrollDown);
        var other = _captureScrollUp ? currentDown : currentUp;
        if (gesture == other)
        {
            SetSettingsStatus(LocalizationService.Get("DuplicateHotkeyWarning"));
            return true;
        }

        if (_captureScrollUp)
        {
            _settingsService.Current.ScrollUpHotkey = gesture.Display;
        }
        else
        {
            _settingsService.Current.ScrollDownHotkey = gesture.Display;
        }

        SaveSettings();
        IsCapturingHotkey = false;
        HotkeyCaptureStateChanged?.Invoke(false);
        HotkeysChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(ScrollUpHotkeyDisplay));
        OnPropertyChanged(nameof(ScrollDownHotkeyDisplay));
        OnPropertyChanged(nameof(ScrollShortcutHint));
        OnPropertyChanged(nameof(PreviousHotkeyTooltip));
        OnPropertyChanged(nameof(NextHotkeyTooltip));
        OnPropertyChanged(nameof(ScrollUpButtonText));
        OnPropertyChanged(nameof(ScrollDownButtonText));
        SetSettingsStatus(LocalizationService.Get("SettingsSaved"));
        return true;
    }

    public void ResetHotkeyDefaults()
    {
        _settingsService.Current.ScrollUpHotkey = HotkeyGesture.DefaultScrollUp.Display;
        _settingsService.Current.ScrollDownHotkey = HotkeyGesture.DefaultScrollDown.Display;
        _settingsService.Current.PageLayout = PageLayout.Auto;
        SaveSettings();
        HotkeysChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(ScrollUpHotkeyDisplay));
        OnPropertyChanged(nameof(ScrollDownHotkeyDisplay));
        OnPropertyChanged(nameof(ScrollShortcutHint));
        OnPropertyChanged(nameof(PreviousHotkeyTooltip));
        OnPropertyChanged(nameof(NextHotkeyTooltip));
        OnPropertyChanged(nameof(PageLayout));
        OnPropertyChanged(nameof(IsLayoutAuto));
        OnPropertyChanged(nameof(IsLayoutColumns));
        OnPropertyChanged(nameof(IsLayoutRows));
        OnPropertyChanged(nameof(IsLayoutGrid));
        SetSettingsStatus(LocalizationService.Get("SettingsSaved"));
    }

    private void SetSettingsStatus(string message)
    {
        SettingsStatusMessage = message;
        HasSettingsStatus = !string.IsNullOrWhiteSpace(message);
    }

    public void RequestWindowNavigation(int direction) =>
        WindowNavigationRequested?.Invoke(direction);

    public void ApplyNavigation(WindowNavigationResult? result)
    {
        _currentNavigation = result;
        if (result is null)
        {
            CurrentWindowTitle = LocalizationService.Get("NoWindowSelected");
            CurrentWindowProcess = LocalizationService.Get("ScrollHint");
            WindowScrollPositionText = "—";
            HasScrollWindow = false;
        }
        else
        {
            CurrentWindowTitle = result.Title;
            CurrentWindowProcess = result.ProcessName;
            WindowScrollPositionText = $"{result.Index + 1} / {result.Count}";
            HasScrollWindow = true;
        }

        foreach (var window in Windows)
        {
            window.IsScrollCurrent = result is not null && window.Handle == result.Handle;
        }
    }

    public void MoveWindowToCapsule(WindowItemViewModel window, CapsuleItemViewModel capsule)
    {
        if (_isDisposed || string.Equals(window.CapsuleId, capsule.Id, StringComparison.Ordinal))
        {
            return;
        }

        var target = ResolveDisplay(capsule.Model.TargetDisplay);
        _windowManager.AssignWindowToCapsule(window.Handle, capsule.Id, target);
        QueueCapture();
        AddActivity(new OptimizationEvent(
            DateTimeOffset.Now,
            window.ProcessName,
            OptimizationEventKind.WindowReassigned,
            LocalizationService.Format("WindowAssignedFormat", capsule.Name) +
            (target is null ? string.Empty : $" · {target.Label}"),
            true));
    }

    private void AddCapsule()
    {
        var nextX = Capsules.Count == 0
            ? 0
            : Capsules.Max(c => c.Model.CanvasX + c.Model.CanvasWidth + 96);
        var display = ResolveDisplay(Capsules.FirstOrDefault()?.Model.TargetDisplay) ?? _displayService.Primary;
        var index = Capsules.Count;
        var definition = new CapsuleDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"Capsule {index + 1}",
            Accent = CapsulePalette[index % CapsulePalette.Length],
            CanvasX = nextX,
            CanvasY = 0,
            CanvasWidth = 1920,
            CanvasHeight = 1080,
            TargetDisplay = display.DeviceName
        };

        _settingsService.Current.Capsules.Add(definition);
        var vm = new CapsuleItemViewModel(
            definition,
            $"{display.Label} · {display.ResolutionLabel}",
            Capsules.Count + 1);
        Capsules.Add(vm);
        _windowManager.ConfigureCapsules(_settingsService.Current.Capsules, ActiveCapsule?.Id ?? vm.Id);
        SaveSettings();
        OnPropertyChanged(nameof(CapsuleSummary));
        OnPropertyChanged(nameof(WindowSummaryText));
        OnPropertyChanged(nameof(WindowCountText));
        AddActivity(new OptimizationEvent(
            DateTimeOffset.Now,
            definition.Name,
            OptimizationEventKind.PolicyStarted,
            LocalizationService.Get("NewPageCreated"),
            true));
    }

    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
    }

    private void UpdateActiveFlags()
    {
        foreach (var capsule in Capsules)
        {
            capsule.IsActive = ReferenceEquals(capsule, _activeCapsule);
        }
    }

    private DisplayInfo? ResolveDisplay(string? deviceName) =>
        _displayService.FindByDevice(deviceName)
        ?? _displayService.Current.FirstOrDefault()
        ?? null;

    private string ResolveDisplayLabel(string? deviceName)
    {
        var display = _displayService.FindByDevice(deviceName);
        return display is null ? "Unassigned" : $"{display.Label} · {display.ResolutionLabel}";
    }

    private void OnWindowsChanged(object? sender, WindowsChangedEventArgs e)
    {
        Dispatch(() => ApplyWindowSnapshot(e.Windows));
    }

    private void ApplyWindowSnapshot(IReadOnlyList<ManagedWindow> snapshot)
    {
        if (_isDisposed)
        {
            return;
        }

        var liveHandles = new HashSet<IntPtr>(snapshot.Select(w => w.Handle));
        foreach (var stale in _windowLookup.Keys.Where(h => !liveHandles.Contains(h)).ToList())
        {
            Windows.Remove(_windowLookup[stale]);
            _windowLookup.Remove(stale);
        }

        foreach (var managed in snapshot)
        {
            var capsule = Capsules.FirstOrDefault(c => c.Id == managed.CapsuleId);
            var display = _displayService.Current.FirstOrDefault(d =>
                string.Equals(d.DeviceName, managed.MonitorDevice, StringComparison.OrdinalIgnoreCase));

            if (_windowLookup.TryGetValue(managed.Handle, out var existing))
            {
                existing.Update(managed, capsule, display);
                existing.IsScrollCurrent = _currentNavigation?.Handle == managed.Handle;
            }
            else
            {
                var item = WindowItemViewModel.Create(managed);
                item.Update(managed, capsule, display);
                item.IsScrollCurrent = _currentNavigation?.Handle == managed.Handle;
                _windowLookup[managed.Handle] = item;
                Windows.Add(item);
            }
        }

        foreach (var capsule in Capsules)
        {
            capsule.WindowCount = snapshot.Count(w => string.Equals(w.CapsuleId, capsule.Id, StringComparison.Ordinal));
            capsule.IsActive = ReferenceEquals(capsule, _activeCapsule);
        }

        WindowCount = snapshot.Count;
        ActiveCapsuleWindowCount = ActiveCapsule is null
            ? 0
            : snapshot.Count(w => string.Equals(w.CapsuleId, ActiveCapsule.Id, StringComparison.Ordinal));

        RefreshFilter();
        ApplyNavigation(_navigator.ResolveCurrent());
    }

    private void OnWindowNavigated(object? sender, WindowNavigationResult e) =>
        Dispatch(() => ApplyNavigation(e));

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(null);
        foreach (var window in Windows)
        {
            window.RefreshLocalization();
        }

        foreach (var capsule in Capsules)
        {
            capsule.RefreshLocalization();
        }
    }

    private void OnTelemetrySample(TelemetrySnapshot sample)
    {
        Dispatch(() => ApplyTelemetry(sample));
    }

    private void ApplyTelemetry(TelemetrySnapshot sample)
    {
        CpuPercent = Math.Round(sample.CpuPercent, 1);
        RamPercent = Math.Round(sample.RamPercent, 1);
        _ramUsedGb = sample.RamUsedGb;
        _ramTotalGb = sample.RamTotalGb;
        if (sample.GpuPercent.HasValue)
        {
            GpuPercent = Math.Round(sample.GpuPercent.Value, 1);
            GpuDetail = LocalizationService.Get("GpuEngine");
        }
        else
        {
            GpuPercent = 0;
            GpuDetail = "Unavailable";
        }

        OnPropertyChanged(nameof(RamDetail));
        OnPropertyChanged(nameof(GpuDetail));

        AppendHistory(_cpuHistory, sample.CpuPercent);
        AppendHistory(_ramHistory, sample.RamPercent);
        AppendHistory(_gpuHistory, sample.GpuPercent ?? 0);
        CpuHistory = _cpuHistory.ToArray();
        RamHistory = _ramHistory.ToArray();
        GpuHistory = _gpuHistory.ToArray();

        SystemStatusText = LocalizationService.Get(
            sample.RamPercent >= 88 || sample.CpuPercent >= 88
                ? "StatusPressure"
                : sample.RamPercent >= 70 || sample.CpuPercent >= 70
                    ? "StatusElevated"
                    : "StatusNominal");
        LastTelemetryLabel = sample.Timestamp.ToString("HH:mm:ss");
        OnPropertyChanged(nameof(SystemStatusText));
    }

    private static void AppendHistory(List<double> history, double value)
    {
        history.Add(Math.Clamp(value, 0, 100));
        if (history.Count > 60)
        {
            history.RemoveRange(0, history.Count - 60);
        }
    }

    private void OnOptimizerActivity(OptimizationEvent optimizationEvent)
    {
        Dispatch(() => AddActivity(optimizationEvent));
    }

    private void AddActivity(OptimizationEvent optimizationEvent)
    {
        var item = new ActivityItemViewModel(optimizationEvent);
        Activity.Insert(0, item);
        _latestActivity = item;
        while (Activity.Count > 8)
        {
            Activity.RemoveAt(Activity.Count - 1);
        }

        OnPropertyChanged(nameof(LastOptimizationText));
        OnPropertyChanged(nameof(LastOptimizationProcess));
        OnPropertyChanged(nameof(LastOptimizationTime));
    }

    private bool FilterWindow(object item)
    {
        if (item is not WindowItemViewModel window)
        {
            return false;
        }

        if (!_showAllWindows && ActiveCapsule is not null &&
            !string.Equals(window.CapsuleId, ActiveCapsule.Id, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_searchText))
        {
            return true;
        }

        var query = _searchText.Trim();
        return window.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               window.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               window.CapsuleName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshFilter()
    {
        WindowsView.Refresh();
        VisibleWindowCount = WindowsView.Cast<object>().Count();
    }

    private void Dispatch(Action action)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (!_dispatcher.HasShutdownStarted)
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
    }

    private void QueueCapture()
    {
        _ = Task.Run(() =>
        {
            try
            {
                _windowManager.CaptureNow();
            }
            catch (Exception ex)
            {
                _log.Error("Post-routing window refresh failed.", ex);
            }
        });
    }

    private void SaveSettings()
    {
        _settingsService.Save(_settingsService.Current);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _windowManager.WindowsChanged -= OnWindowsChanged;
        _telemetry.SampleReceived -= OnTelemetrySample;
        _optimizer.ActivityReported -= OnOptimizerActivity;
        _navigator.Navigated -= OnWindowNavigated;
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    public sealed class MainCommands
    {
        private readonly MainViewModel _vm;

        public MainCommands(MainViewModel vm)
        {
            _vm = vm;
            ActivateCapsule = new RelayCommand(p =>
            {
                if (p is CapsuleItemViewModel capsule)
                {
                    vm.ActivateCapsule(capsule);
                }
            });
            AddCapsule = new RelayCommand(vm.AddCapsule);
            GoToPage = new RelayCommand(p =>
            {
                if (p is CapsuleItemViewModel capsule)
                {
                    vm.GoToPage(capsule);
                }
            });
            FocusWindow = new RelayCommand(p =>
            {
                if (p is WindowItemViewModel window)
                {
                    vm.FocusWindow(window);
                }
            });
            ToggleTheme = new RelayCommand(vm.ToggleTheme);
            Collapse = new RelayCommand(() => vm.CollapseRequested?.Invoke(vm, EventArgs.Empty));
            Exit = new RelayCommand(() => vm.ExitRequested?.Invoke(vm, EventArgs.Empty));
            OptimizeNow = new AsyncRelayCommand(
                async () =>
                {
                    if (!vm.IsOptimizationEnabled)
                    {
                        vm.IsOptimizationEnabled = true;
                    }

                    await vm._optimizer.OptimizeNowAsync();
                },
                ex => vm._log.Error("Manual optimization failed.", ex));
            ShowAllWindows = new RelayCommand(() => vm.ShowAllWindows = true);
            ShowCapsuleWindows = new RelayCommand(() => vm.ShowAllWindows = false);
            ClearSearch = new RelayCommand(() => vm.SearchText = string.Empty);
            NavigatePreviousWindow = new RelayCommand(() => vm.RequestWindowNavigation(-1));
            NavigateNextWindow = new RelayCommand(() => vm.RequestWindowNavigation(1));
            ShowWorkspace = new RelayCommand(() => vm.IsWorkspaceTab = true);
            ShowSettings = new RelayCommand(() => vm.IsWorkspaceTab = false);
            CaptureScrollUp = new RelayCommand(() => vm.BeginHotkeyCapture(true));
            CaptureScrollDown = new RelayCommand(() => vm.BeginHotkeyCapture(false));
            ResetSettings = new RelayCommand(vm.ResetHotkeyDefaults);
        }

        public RelayCommand ActivateCapsule { get; }
        public RelayCommand GoToPage { get; }
        public RelayCommand AddCapsule { get; }
        public RelayCommand FocusWindow { get; }
        public RelayCommand ToggleTheme { get; }
        public RelayCommand Collapse { get; }
        public RelayCommand Exit { get; }
        public AsyncRelayCommand OptimizeNow { get; }
        public RelayCommand ShowAllWindows { get; }
        public RelayCommand ShowCapsuleWindows { get; }
        public RelayCommand ClearSearch { get; }
        public RelayCommand NavigatePreviousWindow { get; }
        public RelayCommand NavigateNextWindow { get; }
        public RelayCommand ShowWorkspace { get; }
        public RelayCommand ShowSettings { get; }
        public RelayCommand CaptureScrollUp { get; }
        public RelayCommand CaptureScrollDown { get; }
        public RelayCommand ResetSettings { get; }
    }
}
