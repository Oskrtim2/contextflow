using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ContextFlow.Infrastructure;
using ContextFlow.Models;
using ContextFlow.Services;
using ContextFlow.ViewModels;
using ContextFlow.Views;

namespace ContextFlow.Services;

/// <summary>
/// Composition root and lifecycle coordinator. Owns all native services and
/// keeps the HUD/bubble transition logic out of the view models.
/// </summary>
public sealed class AppController
{
    private readonly AppPaths _paths;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _hudTransition = new(1, 1);
    private readonly ThemeService _theme = new();

    private SettingsService? _settings;
    private SessionGuard? _sessionGuard;
    private DisplayService? _displays;
    private IconService? _icons;
    private WindowManager? _windowManager;
    private TelemetryService? _telemetry;
    private LocalAiPolicyEngine? _policy;
    private HardwareAIOptimizer? _optimizer;
    private GlobalHotkeyHook? _hotkey;
    private TrayIconService? _tray;
    private MainViewModel? _viewModel;
    private HudWindow? _hud;
    private BubbleWindow? _bubble;
    private HwndSource? _bubbleMessageSource;

    private bool _hudVisible;
    private bool _shutdownStarted;
    private bool _started;
    private bool _suppressHudPlacementSave;
    private DispatcherTimer? _hudPlacementSaveTimer;

    public AppController(AppPaths paths, AppLog log)
    {
        _paths = paths;
        _log = log;
    }

    public MainViewModel ViewModel => _viewModel
        ?? throw new InvalidOperationException("ContextFlow has not completed initialization.");

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _settings = new SettingsService(_paths, _log);
        var settings = _settings.Load();

        _sessionGuard = new SessionGuard(_paths, _log);
        _sessionGuard.RecoverOrphanedState();

        _displays = new DisplayService();
        _displays.Refresh();
        EnsureCapsuleTopology(settings);

        _icons = new IconService();
        _windowManager = new WindowManager(_displays, _icons, _sessionGuard, _log);
        _telemetry = new TelemetryService(_log);
        _policy = new LocalAiPolicyEngine(_log);
        _policy.WarmUp();
        _optimizer = new HardwareAIOptimizer(
            _windowManager,
            _telemetry,
            _policy,
            _settings,
            _sessionGuard,
            () => _settings.Current.Capsules,
            _log);

        _viewModel = new MainViewModel(
            _settings,
            _windowManager,
            _displays,
            _optimizer,
            _telemetry,
            _log);

        _theme.Apply(settings.IsDarkTheme);
        WireViewModel();

        _windowManager.SetCapsuleIsolation(_settings.Current.CapsuleIsolation);
        _optimizer.IsEnabled = _settings.Current.AutoOptimize;
        _windowManager.ConfigureCapsules(
            _settings.Current.Capsules,
            _settings.Current.ActiveCapsuleId);

        _windowManager.Start();
        _telemetry.Start();
        _optimizer.Start();

        CreateWindows(settings);
        StartHotkey();
        CreateTray();

        _log.Info(
            $"ContextFlow ready. Capsules={_settings.Current.Capsules.Count}, " +
            $"Displays={_displays.Current.Count}, Policy={_optimizer.EngineLabel}, " +
            $"Isolation={_settings.Current.CapsuleIsolation}.");

        if (!settings.HasCompletedFirstRun)
        {
            settings.HasCompletedFirstRun = true;
            _settings.Save(settings);
            _ = ShowWelcomeBalloonAsync();
        }
    }

    private void WireViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ThemeChangeRequested += OnThemeChangeRequested;
        _viewModel.CollapseRequested += (_, _) => _ = HideHudAsync();
        _viewModel.ExitRequested += (_, _) => _ = ShutdownAsync();
        _viewModel.HudFocusRequested += (_, _) =>
        {
            if (_hudVisible && _hud is not null)
            {
                _hud.Activate();
                _hud.Focus();
            }
        };
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void CreateWindows(AppSettings settings)
    {
        if (_viewModel is null || _bubble is not null)
        {
            return;
        }

        _hud = new HudWindow(settings.IsDarkTheme)
        {
            DataContext = _viewModel
        };
        _hud.LocationChanged += (_, _) => ScheduleHudPlacementSave();
        _hud.SizeChanged += (_, _) => ScheduleHudPlacementSave();
        _hud.StateChanged += (_, _) => ScheduleHudPlacementSave();

        _bubble = new BubbleWindow
        {
            DataContext = _viewModel
        };
        _bubble.PositionOnScreen(new AppSettingsPosition(
            settings.BubbleLeft.HasValue && settings.BubbleTop.HasValue,
            settings.BubbleLeft ?? 0,
            settings.BubbleTop ?? 0));
        _bubble.OpenRequested += (_, _) => _ = ShowHudAsync();
        _bubble.Moved += OnBubbleMoved;
        _bubble.SourceInitialized += OnBubbleSourceInitialized;
        _bubble.Show();
        _bubble.SnapIntoWorkArea();
        _bubble.PlayShowAnimation();
    }

    private void OnBubbleSourceInitialized(object? sender, EventArgs e)
    {
        if (_bubble is null)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(_bubble).Handle;
        _bubbleMessageSource = HwndSource.FromHwnd(hwnd);
        _bubbleMessageSource?.AddHook(BubbleMessageHook);
        _log.Info("Single-instance activation channel ready.");
    }

    private IntPtr BubbleMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == SingleInstanceService.ShowHudMessage)
        {
            handled = true;
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => _ = ShowHudAsync()));
        }

        return IntPtr.Zero;
    }

    private void OnBubbleMoved(object? sender, BubbleMovedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        _settings.Current.BubbleLeft = e.Left;
        _settings.Current.BubbleTop = e.Top;
        _settings.Save(_settings.Current);
    }

    private void StartHotkey()
    {
        _hotkey = new GlobalHotkeyHook(_log);
        _hotkey.Pressed += (_, _) => _ = ToggleHudAsync();
        var running = _hotkey.Start();
        _viewModel?.SetHotkeyStatus(running, _hotkey.LastError);
        if (!running)
        {
            _log.Warn($"Global hotkey unavailable: {_hotkey.LastError}");
        }
    }

    private void CreateTray()
    {
        if (_viewModel is null)
        {
            return;
        }

        _tray = new TrayIconService(
            showHud: () => _ = ShowHudAsync(),
            toggleAi: () =>
            {
                if (_viewModel is not null)
                {
                    _viewModel.IsOptimizationEnabled = !_viewModel.IsOptimizationEnabled;
                }
            },
            toggleIsolation: () =>
            {
                if (_viewModel is not null)
                {
                    _viewModel.IsCapsuleIsolationEnabled = !_viewModel.IsCapsuleIsolationEnabled;
                }
            },
            toggleTheme: () =>
            {
                if (_viewModel is not null)
                {
                    _viewModel.IsDarkTheme = !_viewModel.IsDarkTheme;
                }
            },
            exit: () => _ = ShutdownAsync());

        UpdateTrayState();
    }

    private async Task ShowWelcomeBalloonAsync()
    {
        try
        {
            await Task.Delay(1200).ConfigureAwait(true);
            _tray?.ShowReadyBalloon();
        }
        catch (Exception ex)
        {
            _log.Error("Could not display the welcome balloon.", ex);
        }
    }

    private async Task ToggleHudAsync()
    {
        if (_hudVisible)
        {
            await HideHudAsync().ConfigureAwait(true);
        }
        else
        {
            await ShowHudAsync().ConfigureAwait(true);
        }
    }

    public async Task ShowHudAsync()
    {
        if (_shutdownStarted || _hud is null)
        {
            return;
        }

        await _hudTransition.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_shutdownStarted)
            {
                return;
            }

            if (_hudVisible)
            {
                _hud.Show();
                _hud.Activate();
                return;
            }

            _hudVisible = true;
            if (_bubble is not null && _bubble.IsVisible)
            {
                await _bubble.PlayHideAnimationAsync().ConfigureAwait(true);
                _bubble.Hide();
            }

            PositionHud();
            _hud.PrepareForOpen();
            _hud.Show();
            _hud.Activate();
            _hud.PlayOpenAnimation();
            _log.Info("HUD expanded.");
        }
        catch (Exception ex)
        {
            _hudVisible = false;
            _log.Error("Could not open the HUD.", ex);
        }
        finally
        {
            _hudTransition.Release();
        }
    }

    public async Task HideHudAsync()
    {
        if (_shutdownStarted || _hud is null || !_hudVisible)
        {
            return;
        }

        await _hudTransition.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!_hudVisible || _shutdownStarted)
            {
                return;
            }

            SaveHudPlacement();
            await _hud.PlayHideAnimationAsync().ConfigureAwait(true);
            _hud.Hide();
            _hudVisible = false;

            if (!_shutdownStarted && _bubble is not null)
            {
                _bubble.Show();
                _bubble.SnapIntoWorkArea();
                _bubble.PlayShowAnimation();
            }

            _log.Info("HUD collapsed to bubble.");
        }
        catch (Exception ex)
        {
            _log.Error("Could not collapse the HUD.", ex);
        }
        finally
        {
            _hudTransition.Release();
        }
    }

    private void PositionHud()
    {
        if (_hud is null || _settings is null)
        {
            return;
        }

        _suppressHudPlacementSave = true;
        try
        {
            // Default to the primary work area on first run. On later runs the
            // saved rectangle is validated against the whole virtual desktop,
            // so a HUD moved to a secondary monitor (including negative X)
            // stays there when it is hidden and shown again.
            var primary = SystemParameters.WorkArea;
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
            var maxWidth = Math.Max(_hud.MinWidth, SystemParameters.VirtualScreenWidth - 16);
            var maxHeight = Math.Max(_hud.MinHeight, SystemParameters.VirtualScreenHeight - 16);

            var width = Math.Min(1320, Math.Max(_hud.MinWidth, primary.Width - 32));
            var height = Math.Min(820, Math.Max(_hud.MinHeight, primary.Height - 32));
            var left = primary.Left + Math.Max(0, (primary.Width - width) / 2);
            var top = primary.Top + Math.Max(0, (primary.Height - height) / 2);

            var stored = _settings.Current;
            if (stored.HudLeft.HasValue && stored.HudTop.HasValue &&
                stored.HudWidth is > 0 && stored.HudHeight is > 0)
            {
                width = Math.Clamp(stored.HudWidth.Value, _hud.MinWidth, maxWidth);
                height = Math.Clamp(stored.HudHeight.Value, _hud.MinHeight, maxHeight);
                left = Math.Clamp(stored.HudLeft.Value, virtualLeft + 8, virtualRight - width - 8);
                top = Math.Clamp(stored.HudTop.Value, virtualTop + 8, virtualBottom - height - 8);
            }

            _hud.WindowState = WindowState.Normal;
            _hud.Width = width;
            _hud.Height = height;
            _hud.Left = left;
            _hud.Top = top;

            if (stored.HudMaximized)
            {
                _hud.WindowState = WindowState.Maximized;
            }
        }
        finally
        {
            _suppressHudPlacementSave = false;
        }
    }

    private void ScheduleHudPlacementSave()
    {
        if (_suppressHudPlacementSave || _hud is null || _settings is null || _shutdownStarted)
        {
            return;
        }

        if (_hudPlacementSaveTimer is null)
        {
            _hudPlacementSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _hudPlacementSaveTimer.Tick += (_, _) =>
            {
                _hudPlacementSaveTimer?.Stop();
                SaveHudPlacement();
            };
        }

        _hudPlacementSaveTimer.Stop();
        _hudPlacementSaveTimer.Start();
    }

    private void SaveHudPlacement()
    {
        if (_hud is null || _settings is null)
        {
            return;
        }

        var bounds = _hud.WindowState == WindowState.Normal
            ? new Rect(_hud.Left, _hud.Top, _hud.Width, _hud.Height)
            : _hud.RestoreBounds;

        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1)
        {
            return;
        }

        _settings.Current.HudLeft = bounds.Left;
        _settings.Current.HudTop = bounds.Top;
        _settings.Current.HudWidth = bounds.Width;
        _settings.Current.HudHeight = bounds.Height;
        _settings.Current.HudMaximized = _hud.WindowState == WindowState.Maximized;
        _settings.Save(_settings.Current);
    }

    private void OnThemeChangeRequested(bool isDark)
    {
        _theme.Apply(isDark);
        _hud?.RefreshComposition(isDark);
        UpdateTrayState();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsOptimizationEnabled)
            or nameof(MainViewModel.IsCapsuleIsolationEnabled)
            or nameof(MainViewModel.IsDarkTheme))
        {
            UpdateTrayState();
        }
    }

    private void UpdateTrayState()
    {
        if (_tray is null || _viewModel is null)
        {
            return;
        }

        _tray.Update(
            _viewModel.IsOptimizationEnabled,
            _viewModel.IsCapsuleIsolationEnabled,
            _viewModel.IsDarkTheme);
    }

    private void EnsureCapsuleTopology(AppSettings settings)
    {
        if (_displays is null)
        {
            return;
        }

        var displays = _displays.Current;
        if (displays.Count == 0)
        {
            return;
        }

        if (settings.Capsules.Count == 0)
        {
            var defaults = new[]
            {
                ("Focus", "#6366F1"),
                ("Research", "#14B8A6"),
                ("Comms", "#F59E0B")
            };

            for (var i = 0; i < defaults.Length; i++)
            {
                var display = displays[i % displays.Count];
                settings.Capsules.Add(new CapsuleDefinition
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = defaults[i].Item1,
                    Accent = defaults[i].Item2,
                    CanvasX = i * 1920,
                    CanvasY = 0,
                    CanvasWidth = 1920,
                    CanvasHeight = 1080,
                    TargetDisplay = display.DeviceName
                });
            }
        }

        var validDevices = displays.Select(d => d.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < settings.Capsules.Count; i++)
        {
            var capsule = settings.Capsules[i];
            if (string.IsNullOrWhiteSpace(capsule.TargetDisplay) || !validDevices.Contains(capsule.TargetDisplay))
            {
                capsule.TargetDisplay = displays[i % displays.Count].DeviceName;
            }
        }

        settings.Sanitize();
        _settings?.Save(settings);
    }

    public async Task ShutdownAsync()
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        _log.Info("ContextFlow shutdown requested.");

        try
        {
            _hotkey?.Dispose();
            _hotkey = null;

            _tray?.Dispose();
            _tray = null;

            if (_bubbleMessageSource is not null)
            {
                _bubbleMessageSource.RemoveHook(BubbleMessageHook);
                _bubbleMessageSource = null;
            }

            if (_settings is not null)
            {
                SaveHudPlacement();
                _settings.Save(_settings.Current);
            }

            if (_hud is not null)
            {
                _hud.Hide();
            }

            if (_bubble is not null)
            {
                _bubble.Hide();
            }

            _hudVisible = false;

            if (_optimizer is not null)
            {
                await _optimizer.DisposeAsync().ConfigureAwait(true);
            }

            if (_windowManager is not null)
            {
                await _windowManager.DisposeAsync().ConfigureAwait(true);
            }

            if (_telemetry is not null)
            {
                await _telemetry.DisposeAsync().ConfigureAwait(true);
            }

            _viewModel?.Dispose();
            _policy?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Error("Shutdown cleanup encountered an error.", ex);
        }
        finally
        {
            try
            {
                _hud?.AllowClose();
                _hud?.Close();
                _bubble?.Close();
            }
            catch (Exception ex)
            {
                _log.Error("Could not close ContextFlow windows.", ex);
            }

            _log.Info("ContextFlow shutdown complete.");
            Application.Current?.Shutdown();
        }
    }

    /// <summary>
    /// Last-resort synchronous restoration used by App.OnExit when Windows is
    /// ending the process outside the normal tray/exit flow.
    /// </summary>
    public void EmergencyRestore()
    {
        try
        {
            _hotkey?.Dispose();
            _tray?.Dispose();
            _optimizer?.RestoreAllPriorities("Emergency application exit.");
            _windowManager?.RestoreAllIsolatedWindows();
            if (_settings is not null)
            {
                _settings.Save(_settings.Current);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Emergency restore failed.", ex);
        }
    }
}
