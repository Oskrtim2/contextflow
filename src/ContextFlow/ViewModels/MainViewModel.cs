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
    private readonly ProcessMonitor _processes;
    private readonly Dictionary<int, AppResourceItemViewModel> _appLookup = new();
    private ProcessSnapshot _processSnapshot = ProcessSnapshot.Empty;
    private AiStats _aiStats;
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
    private HudTab _tab = HudTab.Workspace;
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
        ProcessMonitor processes,
        LocalizationService localization,
        AppLog log)
    {
        _settingsService = settingsService;
        _windowManager = windowManager;
        _displayService = displayService;
        _navigator = navigator;
        _optimizer = optimizer;
        _telemetry = telemetry;
        _processes = processes;
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
        Apps = new ObservableCollection<AppResourceItemViewModel>();
        Insights = new ObservableCollection<InsightItemViewModel>();
        _aiStats = _optimizer.CurrentStats;
        Commands = new MainCommands(this);

        _windowManager.WindowsChanged += OnWindowsChanged;
        _telemetry.SampleReceived += OnTelemetrySample;
        _optimizer.ActivityReported += OnOptimizerActivity;
        _navigator.Navigated += OnWindowNavigated;
        _localization.LanguageChanged += OnLanguageChanged;
        _processes.SnapshotUpdated += OnProcessSnapshot;
        _optimizer.StatsChanged += OnAiStats;

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
    public ObservableCollection<AppResourceItemViewModel> Apps { get; }
    public ObservableCollection<InsightItemViewModel> Insights { get; }
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

    public bool IsAutoMaximizeEnabled
    {
        get => _settingsService.Current.AutoMaximizeWindows;
        set
        {
            if (_settingsService.Current.AutoMaximizeWindows == value)
            {
                return;
            }

            _settingsService.Current.AutoMaximizeWindows = value;
            SaveSettings();
            _windowManager.SetAutoMaximize(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSplitLayoutAvailable));
            HudFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Split page layouts only apply when full-screen mode is off.</summary>
    public bool IsSplitLayoutAvailable => !IsAutoMaximizeEnabled;

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
        get => _tab == HudTab.Workspace;
        set { if (value) SetTab(HudTab.Workspace); }
    }

    public bool IsPerformanceTab
    {
        get => _tab == HudTab.Performance;
        set { if (value) SetTab(HudTab.Performance); }
    }

    public bool IsSettingsTab
    {
        get => _tab == HudTab.Settings;
        set { if (value) SetTab(HudTab.Settings); }
    }

    private void SetTab(HudTab tab)
    {
        if (_tab == tab)
        {
            return;
        }

        _tab = tab;
        if (tab != HudTab.Settings)
        {
            CancelHotkeyCapture();
        }

        OnPropertyChanged(nameof(IsWorkspaceTab));
        OnPropertyChanged(nameof(IsPerformanceTab));
        OnPropertyChanged(nameof(IsSettingsTab));
        if (tab == HudTab.Performance)
        {
            ApplyProcessSnapshot(_processes.Snapshot);
        }
    }

    // ------------------------------------------------------------ AI v2 / performance

    public AiProfile AiProfile
    {
        get => _settingsService.Current.AiProfile;
        set
        {
            if (_settingsService.Current.AiProfile == value)
            {
                return;
            }

            _settingsService.Current.AiProfile = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsProfileConservative));
            OnPropertyChanged(nameof(IsProfileBalanced));
            OnPropertyChanged(nameof(IsProfileAggressive));
            OnPropertyChanged(nameof(AiProfileDescription));
            _optimizer.Wake();
            AddActivity(new OptimizationEvent(
                DateTimeOffset.Now,
                "ContextFlow",
                OptimizationEventKind.PolicyStarted,
                $"AI profile: {value}",
                true,
                "AiProfileChangedFormat",
                [LocalizationService.Get("Profile" + value)]));
        }
    }

    public bool IsProfileConservative
    {
        get => AiProfile == AiProfile.Conservative;
        set { if (value) AiProfile = AiProfile.Conservative; }
    }

    public bool IsProfileBalanced
    {
        get => AiProfile == AiProfile.Balanced;
        set { if (value) AiProfile = AiProfile.Balanced; }
    }

    public bool IsProfileAggressive
    {
        get => AiProfile == AiProfile.Aggressive;
        set { if (value) AiProfile = AiProfile.Aggressive; }
    }

    public string AiProfileDescription => LocalizationService.Get("Profile" + AiProfile + "Detail");

    public PressureLevel AiPressure => _aiStats.Pressure;
    public string AiPressureText => LocalizationService.Get("Pressure" + _aiStats.Pressure);
    public string AiIntervalText => LocalizationService.Format("AiIntervalFormat", _aiStats.PassInterval.TotalSeconds);
    public string AiOptimizedNowText => _aiStats.OptimizedNow.ToString();
    public string AiReclaimedText => AppResourceItemViewModel.FormatMemory(_aiStats.ReclaimedMbSession);
    public string AiCpuContainedText => $"{_aiStats.CpuContainedPercent:0.#}%";
    public string AiActionsText => _aiStats.ActionsSession.ToString();
    public string AiLearnedAppsText => _aiStats.LearnedApps.ToString();
    public string AiAccuracyText => $"{_aiStats.Accuracy * 100:0}%";
    public string AiPowerText => LocalizationService.Get(_aiStats.OnBattery ? "PowerBattery" : "PowerAc");

    private string _appsSummaryText = string.Empty;

    public string AppsSummaryText
    {
        get => _appsSummaryText;
        private set => SetProperty(ref _appsSummaryText, value);
    }

    private void OnProcessSnapshot(ProcessSnapshot snapshot) => Dispatch(() => ApplyProcessSnapshot(snapshot));

    private void OnAiStats(AiStats stats) => Dispatch(() => ApplyAiStats(stats));

    private void ApplyAiStats(AiStats stats)
    {
        _aiStats = stats;
        OnPropertyChanged(nameof(AiPressure));
        OnPropertyChanged(nameof(AiPressureText));
        OnPropertyChanged(nameof(AiIntervalText));
        OnPropertyChanged(nameof(AiOptimizedNowText));
        OnPropertyChanged(nameof(AiReclaimedText));
        OnPropertyChanged(nameof(AiCpuContainedText));
        OnPropertyChanged(nameof(AiActionsText));
        OnPropertyChanged(nameof(AiLearnedAppsText));
        OnPropertyChanged(nameof(AiAccuracyText));
        OnPropertyChanged(nameof(AiPowerText));
    }

    private void ApplyProcessSnapshot(ProcessSnapshot snapshot)
    {
        if (_isDisposed || snapshot.Processes.Count == 0)
        {
            return;
        }

        _processSnapshot = snapshot;

        // Window rows: per-app CPU and memory.
        var iconsByRoot = new Dictionary<int, System.Windows.Media.ImageSource>();
        foreach (var window in Windows)
        {
            var family = snapshot.FamilyOf(window.ProcessId);
            window.UpdateResources(family, family is not null && _optimizer.GetState(family) == AppOptimizationState.Optimized);
            if (family is not null && window.Icon is not null)
            {
                iconsByRoot.TryAdd(family.RootProcessId, window.Icon);
            }
        }

        if (!IsPerformanceTab)
        {
            return;
        }

        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        bool IsUserApp(AppFamily f) =>
            snapshot.Processes.TryGetValue(f.RootProcessId, out var root) && root.SessionId == sessionId;

        var candidates = snapshot.Families.Values
            .Where(f => iconsByRoot.ContainsKey(f.RootProcessId) ||
                        (IsUserApp(f) && !HardwareAIOptimizer.IsProtectedName(f.Name)))
            .ToList();

        // Optimized and user-protected apps stay listed even after trimming
        // shrinks them, so the user can always restore or unprotect them.
        // They are listed first: what the AI touched is the most relevant info here.
        var pinned = candidates
            .Where(f => _optimizer.GetState(f) == AppOptimizationState.Optimized || _optimizer.IsExcluded(f.Name))
            .OrderByDescending(f => _optimizer.GetState(f) == AppOptimizationState.Optimized)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var families = pinned
            .Concat(candidates.OrderByDescending(f => f.MemoryMb + f.CpuPercent * 60).Take(14))
            .DistinctBy(f => f.RootProcessId)
            .ToList();

        var maxCpu = families.Count == 0 ? 1 : families.Max(f => f.CpuPercent);
        var maxMemory = families.Count == 0 ? 1 : families.Max(f => f.MemoryMb);
        var keep = families.Select(f => f.RootProcessId).ToHashSet();
        foreach (var stale in _appLookup.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            Apps.Remove(_appLookup[stale]);
            _appLookup.Remove(stale);
        }

        for (var i = 0; i < families.Count; i++)
        {
            var family = families[i];
            if (!_appLookup.TryGetValue(family.RootProcessId, out var item))
            {
                item = new AppResourceItemViewModel(family);
                _appLookup[family.RootProcessId] = item;
                Apps.Insert(Math.Min(i, Apps.Count), item);
            }
            else
            {
                var current = Apps.IndexOf(item);
                if (current != i && i < Apps.Count)
                {
                    Apps.Move(current, i);
                }
            }

            item.Update(family, _optimizer.GetState(family), _optimizer.IsExcluded(family.Name), maxCpu, maxMemory);
            if (item.Icon is null && iconsByRoot.TryGetValue(family.RootProcessId, out var icon))
            {
                item.Icon = icon;
            }
        }

        var userFamilies = snapshot.Families.Values.Count(IsUserApp);
        AppsSummaryText = LocalizationService.Format("AppsSummaryFormat", userFamilies, snapshot.Processes.Count);
        BuildInsights(families);
    }

    private void BuildInsights(IReadOnlyList<AppFamily> families)
    {
        var insights = new List<InsightItemViewModel>();

        if (_aiStats.Pressure >= PressureLevel.High)
        {
            insights.Add(new InsightItemViewModel(
                "⚡",
                LocalizationService.Format("InsightPressureTitle", RamPercent),
                LocalizationService.Get("InsightPressureDetail"),
                "warning"));
        }

        var hog = families
            .Where(f => f.SustainedCpuPercent >= 10 && _optimizer.GetState(f) is not AppOptimizationState.Active)
            .OrderByDescending(f => f.SustainedCpuPercent)
            .FirstOrDefault();
        if (hog is not null)
        {
            insights.Add(new InsightItemViewModel(
                "▲",
                LocalizationService.Format("InsightCpuTitle", hog.Name, hog.SustainedCpuPercent),
                LocalizationService.Get(_optimizer.GetState(hog) == AppOptimizationState.Optimized
                    ? "InsightCpuDetailDone"
                    : "InsightCpuDetail"),
                "warning"));
        }

        var heavy = families.OrderByDescending(f => f.MemoryMb).FirstOrDefault();
        if (heavy is not null && (heavy.MemoryMb >= 1200 || (_ramTotalGb > 0 && heavy.MemoryMb / 1024 >= _ramTotalGb * 0.15)))
        {
            insights.Add(new InsightItemViewModel(
                "◆",
                LocalizationService.Format("InsightMemoryTitle", heavy.Name, AppResourceItemViewModel.FormatMemory(heavy.MemoryMb), heavy.ProcessIds.Count),
                LocalizationService.Get("InsightMemoryDetail"),
                "accent"));
        }

        foreach (var audio in families.Where(f => _optimizer.GetState(f) == AppOptimizationState.PlayingAudio).Take(1))
        {
            insights.Add(new InsightItemViewModel(
                "♪",
                LocalizationService.Format("InsightAudioTitle", audio.Name),
                LocalizationService.Get("InsightAudioDetail"),
                "accent"));
        }

        if (_aiStats.OptimizedNow > 0 || _aiStats.ReclaimedMbSession >= 1)
        {
            insights.Add(new InsightItemViewModel(
                "✓",
                LocalizationService.Format("InsightSavingsTitle", _aiStats.OptimizedNow, AiReclaimedText),
                LocalizationService.Get("InsightSavingsDetail"),
                "success"));
        }

        insights.Add(new InsightItemViewModel(
            "✦",
            LocalizationService.Format("InsightLearningTitle", _aiStats.LearnedApps, AiAccuracyText),
            LocalizationService.Get("InsightLearningDetail"),
            "success"));

        if (_aiStats.OnBattery)
        {
            insights.Add(new InsightItemViewModel(
                "▮",
                LocalizationService.Get("InsightBatteryTitle"),
                LocalizationService.Get("InsightBatteryDetail"),
                "accent"));
        }

        var top = insights.Take(5).ToList();
        if (top.SequenceEqual(Insights))
        {
            return;
        }

        Insights.Clear();
        foreach (var insight in top)
        {
            Insights.Add(insight);
        }
    }

    private void ToggleAppOptimization(AppResourceItemViewModel app)
    {
        if (app.IsOptimized)
        {
            _optimizer.RestoreFamilyNow(app.RootProcessId);
            return;
        }

        if (!IsOptimizationEnabled)
        {
            IsOptimizationEnabled = true;
        }

        var root = app.RootProcessId;
        _ = Task.Run(() =>
        {
            try
            {
                _optimizer.OptimizeFamilyNow(root);
            }
            catch (Exception ex)
            {
                _log.Error("Manual app optimization failed.", ex);
            }
        });
    }

    private void ToggleAppProtection(AppResourceItemViewModel app)
    {
        var nowProtected = _optimizer.ToggleProtection(app.Family);
        AddActivity(new OptimizationEvent(
            DateTimeOffset.Now,
            app.Name,
            OptimizationEventKind.Skipped,
            nowProtected ? "Protected" : "Unprotected",
            true,
            nowProtected ? "AppProtectedDetail" : "AppUnprotectedDetail"));
        ApplyProcessSnapshot(_processes.Snapshot);
    }

    private static MessageBoxResult Confirm(string message, MessageBoxImage image)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return owner is null
            ? MessageBox.Show(message, "ContextFlow", MessageBoxButton.YesNo, image)
            : MessageBox.Show(owner, message, "ContextFlow", MessageBoxButton.YesNo, image);
    }

    private void EndApp(AppResourceItemViewModel app)
    {
        var family = app.Family;
        var message = LocalizationService.Format("EndAppConfirmFormat", app.Name, family.ProcessIds.Count);
        if (Confirm(message, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var ended = _optimizer.TryEndFamily(family, out var error);
        AddActivity(new OptimizationEvent(
            DateTimeOffset.Now,
            app.Name,
            ended ? OptimizationEventKind.ProcessEnded : OptimizationEventKind.Error,
            ended ? "Ended" : error ?? "Error",
            ended,
            ended ? "AppEndedDetail" : "AppEndFailedDetail",
            [family.ProcessIds.Count, error]));
        ApplyProcessSnapshot(_processes.SampleNow());
    }

    private void ResetLearning()
    {
        if (Confirm(LocalizationService.Get("ResetLearningConfirm"), MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _optimizer.Usage.Reset();
        ApplyAiStats(_optimizer.CurrentStats);
        AddActivity(new OptimizationEvent(
            DateTimeOffset.Now,
            "ContextFlow",
            OptimizationEventKind.LearningAdjusted,
            "Learning reset",
            true,
            "LearningResetDetail"));
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

        for (var i = 0; i < Activity.Count; i++)
        {
            Activity[i] = new ActivityItemViewModel(Activity[i].Event);
        }

        Insights.Clear();
        ApplyProcessSnapshot(_processes.Snapshot);
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
        _processes.SnapshotUpdated -= OnProcessSnapshot;
        _optimizer.StatsChanged -= OnAiStats;
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
            ShowSettings = new RelayCommand(() => vm.IsSettingsTab = true);
            ShowPerformance = new RelayCommand(() => vm.IsPerformanceTab = true);
            ToggleAppOptimization = new RelayCommand(p =>
            {
                if (p is AppResourceItemViewModel app)
                {
                    vm.ToggleAppOptimization(app);
                }
            });
            ToggleAppProtection = new RelayCommand(p =>
            {
                if (p is AppResourceItemViewModel app)
                {
                    vm.ToggleAppProtection(app);
                }
            });
            EndApp = new RelayCommand(p =>
            {
                if (p is AppResourceItemViewModel app)
                {
                    vm.EndApp(app);
                }
            });
            ResetLearning = new RelayCommand(vm.ResetLearning);
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
        public RelayCommand ShowPerformance { get; }
        public RelayCommand ToggleAppOptimization { get; }
        public RelayCommand ToggleAppProtection { get; }
        public RelayCommand EndApp { get; }
        public RelayCommand ResetLearning { get; }
        public RelayCommand CaptureScrollUp { get; }
        public RelayCommand CaptureScrollDown { get; }
        public RelayCommand ResetSettings { get; }
    }
}

internal enum HudTab
{
    Workspace,
    Performance,
    Settings
}
