using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
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
    private readonly HardwareAIOptimizer _optimizer;
    private readonly TelemetryService _telemetry;
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
    private string _gpuDetail = "Waiting for PDH";
    private string _systemStatusText = "Nominal";
    private string _lastTelemetryLabel = "—";
    private IReadOnlyList<double> _cpuHistoryView = Array.Empty<double>();
    private IReadOnlyList<double> _ramHistoryView = Array.Empty<double>();
    private IReadOnlyList<double> _gpuHistoryView = Array.Empty<double>();
    private int _windowCount;
    private int _activeCapsuleWindowCount;
    private int _visibleWindowCount;
    private string _hotkeyStatusLabel = "INITIALIZING";
    private string _hotkeyDetail = "Ctrl + E + F";
    private bool _hotkeyReady;

    public MainViewModel(
        SettingsService settingsService,
        WindowManager windowManager,
        DisplayService displayService,
        HardwareAIOptimizer optimizer,
        TelemetryService telemetry,
        AppLog log)
    {
        _settingsService = settingsService;
        _windowManager = windowManager;
        _displayService = displayService;
        _optimizer = optimizer;
        _telemetry = telemetry;
        _log = log;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _isDarkTheme = _settingsService.Current.IsDarkTheme;
        _isOptimizationEnabled = _settingsService.Current.AutoOptimize;
        _isIsolationEnabled = _settingsService.Current.CapsuleIsolation;

        _displayService.Refresh();
        Displays = new ObservableCollection<DisplayItemViewModel>(
            _displayService.Current.Select(d => new DisplayItemViewModel(d)));

        Capsules = new ObservableCollection<CapsuleItemViewModel>(
            _settingsService.Current.Capsules.Select(c =>
                new CapsuleItemViewModel(c, ResolveDisplayLabel(c.TargetDisplay))));

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

        _windowManager.ConfigureCapsules(
            _settingsService.Current.Capsules,
            _activeCapsule?.Id ?? string.Empty);

        AddActivity(new OptimizationEvent(
            DateTimeOffset.Now,
            "ContextFlow",
            OptimizationEventKind.PolicyStarted,
            $"Local policy online · {_optimizer.EngineLabel} · no cloud connectivity.",
            true));

        if (_telemetry.LastSample.Timestamp != default)
        {
            ApplyTelemetry(_telemetry.LastSample);
        }

        ApplyWindowSnapshot(_windowManager.Snapshot);
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

    /// <summary>
    /// Raised after activating a capsule restores minimized windows so the
    /// controller can return keyboard focus to the topmost HUD.
    /// </summary>
    public event EventHandler? HudFocusRequested;

    public CapsuleItemViewModel? ActiveCapsule => _activeCapsule;

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
                value ? "Local AI optimization resumed." : "Local AI optimization paused; priorities restored.",
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

    public string AiStatusText => IsOptimizationEnabled ? "ACTIVE" : "PAUSED";

    public string AiStatusDetail => IsOptimizationEnabled
        ? "Watching inactive capsules every 10 seconds"
        : "No process scheduling changes are being made";

    public string IsolationStatusText => IsCapsuleIsolationEnabled ? "COMPOSITION THROTTLED" : "COMPOSITION ON DEMAND";

    public bool HotkeyReady
    {
        get => _hotkeyReady;
        private set => SetProperty(ref _hotkeyReady, value);
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

    public string CpuDetail => $"{Environment.ProcessorCount} logical processors";
    public string RamDetail => _ramTotalGb > 0 ? $"{_ramUsedGb:0.0} / {_ramTotalGb:0.0} GB" : "Reading memory";

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
        private set => SetProperty(ref _systemStatusText, value);
    }

    public string LastTelemetryLabel
    {
        get => _lastTelemetryLabel;
        private set => SetProperty(ref _lastTelemetryLabel, value);
    }

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

    public string WindowSummaryText => $"{WindowCount} windows · {Capsules.Count} capsules";

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
        ? "1 window"
        : $"{ActiveCapsuleWindowCount} windows";

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

    public string VisibleWindowCountText => VisibleWindowCount == 1 ? "1 window" : $"{VisibleWindowCount} windows";
    public bool HasVisibleWindows => VisibleWindowCount > 0;
    public bool HasNoVisibleWindows => VisibleWindowCount == 0;

    public string ActiveCapsuleName => ActiveCapsule?.Name ?? "No capsule";
    public string ActiveCapsuleDisplayLabel => ActiveCapsule?.TargetDisplayLabel ?? "—";

    public string CapsuleSummary => Capsules.Count == 1 ? "1 capsule" : $"{Capsules.Count} capsules";
    public string DisplaySummary => Displays.Count == 1 ? "1 display" : $"{Displays.Count} displays";

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

            var result = _windowManager.MoveCapsuleWindowsToDisplay(ActiveCapsule.Id, display);
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveCapsuleDisplayLabel));

            if (result.Moved > 0)
            {
                AddActivity(new OptimizationEvent(
                    DateTimeOffset.Now,
                    ActiveCapsule.Name,
                    OptimizationEventKind.CapsuleRouted,
                    $"Routed {result.Moved} window(s) to {display.Label} · {display.ResolutionLabel}.",
                    true));
                QueueCapture();
            }
        }
    }

    public string LastOptimizationText => _latestActivity?.Detail ?? "Awaiting first policy decision";

    public string LastOptimizationProcess => _latestActivity?.ProcessName ?? "LOCAL POLICY";

    public string LastOptimizationTime => _latestActivity?.TimeLabel ?? "—";

    public string SessionStartLabel => DateTime.Now.ToString("ddd, dd MMM · HH:mm");

    public string BuildLabel => "v1.0.0 · local-first";

    public string EngineLabel => _optimizer.EngineLabel;

    public bool IsOnnxAvailable => _optimizer.IsOnnxAvailable;

    public void SetHotkeyStatus(bool ready, string? error = null)
    {
        HotkeyReady = ready;
        HotkeyStatusLabel = ready ? "GLOBAL HOTKEY READY" : "HOTKEY UNAVAILABLE";
        HotkeyDetail = ready
            ? "Ctrl + E + F"
            : error ?? "Another application may be capturing keyboard input.";
        OnPropertyChanged(nameof(HotkeyReady));
    }

    public void ActivateCapsule(CapsuleItemViewModel capsule)
    {
        if (_isDisposed || ReferenceEquals(capsule, _activeCapsule))
        {
            if (ReferenceEquals(capsule, _activeCapsule))
            {
                RefreshFilter();
            }

            return;
        }

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

        var target = ResolveDisplay(capsule.Model.TargetDisplay);
        if (target is not null)
        {
            var move = _windowManager.MoveCapsuleWindowsToDisplay(capsule.Id, target);
            if (move.Moved > 0)
            {
                _log.Info($"Activated capsule '{capsule.Name}' and routed {move.Moved} window(s) to {target.Label}.");
                QueueCapture();
            }
        }

        SaveSettings();
        RefreshFilter();
        OnPropertyChanged(nameof(ActiveCapsuleDisplayLabel));
        HudFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    public void FocusWindow(WindowItemViewModel window)
    {
        var capsule = Capsules.FirstOrDefault(c => c.Id == window.CapsuleId);
        if (capsule is not null && !ReferenceEquals(capsule, _activeCapsule))
        {
            ActivateCapsule(capsule);
        }

        _windowManager.FocusWindow(window.Handle);
        CollapseRequested?.Invoke(this, EventArgs.Empty);
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
            $"Window assigned to capsule '{capsule.Name}'" +
            (target is null ? "." : $" on {target.Label}."),
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
        var vm = new CapsuleItemViewModel(definition, $"{display.Label} · {display.ResolutionLabel}");
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
            "New workflow capsule created.",
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
            }
            else
            {
                var item = WindowItemViewModel.Create(managed);
                item.Update(managed, capsule, display);
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
            GpuDetail = "GPU engine utilization";
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

        SystemStatusText = sample.RamPercent >= 88 || sample.CpuPercent >= 88
            ? "Pressure"
            : sample.RamPercent >= 70 || sample.CpuPercent >= 70
                ? "Elevated"
                : "Nominal";
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
        }

        public RelayCommand ActivateCapsule { get; }
        public RelayCommand AddCapsule { get; }
        public RelayCommand FocusWindow { get; }
        public RelayCommand ToggleTheme { get; }
        public RelayCommand Collapse { get; }
        public RelayCommand Exit { get; }
        public AsyncRelayCommand OptimizeNow { get; }
        public RelayCommand ShowAllWindows { get; }
        public RelayCommand ShowCapsuleWindows { get; }
        public RelayCommand ClearSearch { get; }
    }
}
