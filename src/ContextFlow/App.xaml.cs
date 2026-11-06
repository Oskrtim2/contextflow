using System.Windows;
using System.Windows.Threading;
using ContextFlow.Infrastructure;
using ContextFlow.Services;

namespace ContextFlow;

public partial class App : Application
{
    private AppPaths? _paths;
    private AppLog? _log;
    private SingleInstanceService? _singleInstance;
    private AppController? _controller;
    private bool _fatalShutdown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _paths = new AppPaths();
        _paths.EnsureCreated();
        _log = new AppLog(_paths);
        _log.Info("ContextFlow starting.");

        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimaryInstance)
        {
            _log.Info("Another ContextFlow instance is already running; signaling it to show the HUD.");
            SingleInstanceService.SignalExistingInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            _controller = new AppController(_paths, _log);
            await _controller.StartAsync();
            ScheduleDiagnosticExit(e.Args);
        }
        catch (Exception ex)
        {
            _log.Error("Fatal startup failure.", ex);
            MessageBox.Show(
                "ContextFlow could not start.\n\n" + ex.Message +
                "\n\nDiagnostic details were written to:\n" + _paths.LogFile,
                "ContextFlow",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _fatalShutdown = true;
            Shutdown(1);
        }
    }

    /// <summary>
    /// Headless verification hook for packaged smoke tests:
    /// ContextFlow.exe --exit-after 8
    /// Performs the same restoration path as tray Exit.
    /// </summary>
    private void ScheduleDiagnosticExit(string[] args)
    {
        var index = Array.IndexOf(args, "--exit-after");
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var seconds))
        {
            return;
        }

        seconds = Math.Clamp(seconds, 1, 3600);
        _log?.Info($"Diagnostic auto-exit scheduled in {seconds} second(s).");
        _ = AutoExitAsync(seconds);
    }

    private async Task AutoExitAsync(int seconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            if (_controller is not null)
            {
                await _controller.ShutdownAsync();
            }
        }
        catch (Exception ex)
        {
            _log?.Error("Diagnostic auto-exit failed.", ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;
        if (_fatalShutdown)
        {
            return;
        }

        _fatalShutdown = true;
        _controller?.EmergencyRestore();
        MessageBox.Show(
            "ContextFlow encountered an unexpected error and will close to protect your desktop state.\n\n" +
            e.Exception.Message + "\n\nDetails: " + _paths?.LogFile,
            "ContextFlow",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(1);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled application exception.", e.ExceptionObject as Exception);
        _controller?.EmergencyRestore();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.EmergencyRestore();
        _singleInstance?.Dispose();
        _log?.Info($"ContextFlow exited with code {e.ApplicationExitCode}.");
        _log?.Dispose();
        base.OnExit(e);
    }
}
