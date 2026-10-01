using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Security.App.Services;
using Security.App.ViewModels;
using Security.Core.Enums;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Data;
using Security.Face;
using Security.Infrastructure;
using Security.Infrastructure.Logging;
using Serilog;
using Serilog.Events;

namespace Security.App;

/// <summary>
/// Composition root. Builds the generic host, wires DI, and owns the
/// application lifetime. All business logic lives in Security.* libraries —
/// this class only sequences startup/shutdown.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private CancellationTokenSource? _startupCts;
    private SingleInstanceGuard? _instanceGuard;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;
    private Views.SecurityAlertWindow? _alertWindow;

    /// <summary>Set by the tray's Exit command so the close-to-tray rule lets the window really close.</summary>
    private bool _exitRequested;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        InstallGlobalExceptionHandlers();

        // 0. Single instance: the first copy owns the session; a second copy
        //    asks it to come forward and quits before touching DB/camera/host.
        _instanceGuard = new SingleInstanceGuard();
        if (!_instanceGuard.IsPrimary)
        {
            try
            {
                _instanceGuard.SignalActivation();
            }
            catch (Exception)
            {
                // No primary listening — exiting anyway is still correct.
            }

            _instanceGuard.Dispose();
            _instanceGuard = null;
            Shutdown();
            return;
        }

        _instanceGuard.ActivationRequested += (_, _) =>
            Dispatcher.BeginInvoke(new Action(ShowMainWindow));

        _startupCts = new CancellationTokenSource();
        var token = _startupCts.Token;

        try
        {
            _host = BuildHost();

            var logger = _host.Services.GetRequiredService<ILogger<App>>();

            // 1. Database must exist before settings/events can load.
            await _host.Services.EnsureSecurityDatabaseAsync(token);

            // 2. Overlay persisted user settings onto the appsettings defaults.
            await _host.Services.GetRequiredService<ISettingsService>().LoadAsync(token);

            // 2b. The camera is configured from CameraOptions, which the
            //     container builds before settings exist — copy the persisted
            //     capture size across so a restarted session honours it
            //     instead of silently falling back to 1280x720.
            var effectiveSettings = _host.Services.GetRequiredService<ISettingsService>();
            var cameraOptions = _host.Services.GetService<CameraOptions>();
            if (cameraOptions is not null)
            {
                cameraOptions.Width = effectiveSettings.Current.CameraWidth;
                cameraOptions.Height = effectiveSettings.Current.CameraHeight;
                logger.LogInformation(
                    "Capture resolution applied: {Width}x{Height}",
                    cameraOptions.Width, cameraOptions.Height);
            }

            // 3. Shell first so the operator gets immediate feedback; model
            //    acquisition can take a while on a cold start.
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            _mainWindow = window;
            window.Show();

            // 3b. Notification-area icon (Phase 3). Menu commands route back
            //     through the host's services; the status line mirrors the
            //     monitor's own text verbatim.
            InitializeTray(logger);

            // Kick the initial screen's refresh (engine status + recent events)
            // now that bindings are live. Runs in the background — startup must
            // not block on it.
            _ = _host.Services.GetRequiredService<MainViewModel>().OnShellReadyAsync();

            _ = LoadEngineAsync(logger, token);

            // 4. Start the generic host: this launches the background
            //    monitoring loop (session observation, camera lifecycle,
            //    health, retention). It yields immediately — camera auto-start
            //    is deliberately delayed inside the monitor so it never races
            //    the shell's first refresh above.
            await _host.StartAsync(token);

            logger.LogInformation("SECURITY.EXE {Version} started", VersionInfo.Display);
        }
        catch (Exception ex)
        {
            // Startup failures must be reported, never swallowed silently.
            Log.Fatal(ex, "SECURITY.EXE failed to start");

            MessageBox.Show(
                "Security could not start.\n\n" + ex.Message +
                "\n\nSee the logs folder for details.",
                "Security — startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    /// <summary>
    /// Last-resort handlers. A value converter, a binding, or a fire-and-forget
    /// task throwing on the dispatcher is not a reason to terminate the
    /// session — Phase 1's contract is that the operator keeps a working window
    /// and the details land in logs/.
    ///
    /// Observed in testing: an invalid colour string in one converter crashed
    /// the entire application mid-session. These handlers turn that class of
    /// fault into a logged error instead.
    /// </summary>
    private void InstallGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI exception (recovered, continuing)");
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved background task exception");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Fatal(ex, "Unhandled exception (IsTerminating={Terminating})", args.IsTerminating);
            else
                Log.Fatal("Unhandled exception: {Exception}", args.ExceptionObject);
        };
    }

    /// <summary>
    /// Acquire/verify the ONNX models in the background, then refresh the
    /// dashboard's engine tile. Never blocks or crashes the UI.
    /// </summary>
    private async Task LoadEngineAsync(Microsoft.Extensions.Logging.ILogger logger, CancellationToken token)
    {
        try
        {
            var coordinator = _host!.Services.GetRequiredService<CameraCoordinator>();

            var modelsOk = await _host.Services.EnsureFaceModelsAsync(token).ConfigureAwait(true);

            if (!modelsOk)
            {
                logger.LogWarning(
                    "Face models could not be prepared. Recognition will report \"Not Ready\". " +
                    "Run scripts/download-models.ps1 or connect to the internet to fetch them.");
                await coordinator.RefreshEngineStatusAsync(token).ConfigureAwait(true);
                return;
            }

            await coordinator.RefreshEngineStatusAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutdown during startup.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Model preflight failed; the app continues without recognition");
        }
    }

    /// <summary>
    /// Create the tray icon and connect its menu to the host's services:
    /// open/restore the window, pause/resume monitoring, navigate to Events
    /// or Settings, and Exit (a real shutdown — camera stop, host stop,
    /// ApplicationStopped). The status line mirrors
    /// <c>MonitoringStatusText</c> verbatim on every monitor state change.
    /// </summary>
    private void InitializeTray(Microsoft.Extensions.Logging.ILogger logger)
    {
        try
        {
            var monitor = _host!.Services.GetRequiredService<SecurityMonitorService>();
            var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
            var settings = _host.Services.GetRequiredService<ISettingsService>();

            var iconPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "Resources", "security.ico");

            _tray = new TrayIconService(iconPath);

            _tray.Commands.OpenWindowRequested += (_, _) => ShowMainWindow();
            _tray.Commands.OpenEventsRequested += (_, _) =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ShowMainWindow();
                    mainViewModel.NavigateTo("events");
                }));
            _tray.Commands.OpenSettingsRequested += (_, _) =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ShowMainWindow();
                    mainViewModel.NavigateTo("settings");
                }));
            _tray.Commands.ExitRequested += (_, _) => ExitFromTray();

            _tray.Commands.PauseToggleRequested += async (_, pause) =>
            {
                try
                {
                    await monitor.SetMonitoringPausedAsync(pause);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Tray pause toggle failed");
                }
            };

            // Status line + balloon notifications from the monitor.
            monitor.StateChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                _tray?.UpdateStatus(monitor.MonitoringStatusText, monitor.IsPaused)));

            monitor.NotificationRequested += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                // The monitor already applied the cooldown, the active-
                // monitoring gate and the locked-session rule. Wording is
                // fixed and neutral — "Unknown person detected.", never
                // "Intruder".
                if (settings.Current.DesktopNotifications)
                    _tray?.ShowNotification("Security", "Unknown person detected.");

                ShowSecurityAlertWindow(monitor);
            }));

            _tray.UpdateStatus(monitor.MonitoringStatusText, monitor.IsPaused);

            logger.LogInformation("Tray icon ready");
        }
        catch (Exception ex)
        {
            // The app must work without a tray icon (e.g. icon load failure).
            logger.LogWarning(ex, "Tray icon could not be created; continuing without it");
        }
    }

    /// <summary>Restore the window from the tray, a second instance, or a balloon click.</summary>
    private void ShowMainWindow()
    {
        if (_mainWindow is null)
            return;

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
    }

    /// <summary>
    /// Open the unknown-face alert window for this notification. Shown only in
    /// the NORMAL desktop session — never on the lock screen or the Secure
    /// Desktop — only while desktop notifications are enabled, and only when
    /// the main window is not already on screen (the in-app banner covers
    /// that case). One instance at a time: a newer notification replaces the
    /// previous alert instead of stacking windows.
    /// </summary>
    private void ShowSecurityAlertWindow(SecurityMonitorService monitor)
    {
        // Defence in depth: the monitor already refuses to notify while the
        // session is locked; re-check before any UI is created.
        if (monitor.CurrentSessionState == SessionState.Locked)
            return;

        if (_host is null)
            return;

        try
        {
            var settings = _host.Services.GetRequiredService<ISettingsService>();
            if (!settings.Current.DesktopNotifications)
                return;

            // Window visible = the in-app banner already carries the alert.
            if (_mainWindow is { IsVisible: true } && _mainWindow.WindowState != WindowState.Minimized)
                return;

            var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
            var window = new Views.SecurityAlertWindow();
            window.OpenEventsRequested += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                ShowMainWindow();
                mainViewModel.NavigateTo("events");
            }));
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_alertWindow, window))
                    _alertWindow = null;
            };

            _alertWindow?.Close();
            _alertWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Security alert window could not be shown");
        }
    }

    /// <summary>
    /// Tray Exit: a full shutdown (not hide-to-tray). OnExit then stops the
    /// camera, records ApplicationStopped, and stops the host.
    /// </summary>
    private void ExitFromTray()
    {
        _exitRequested = true;
        Shutdown();
    }

    /// <summary>
    /// Called by MainWindow's Closing handler: with close-to-tray enabled, X
    /// hides the window and keeps monitoring; otherwise (or when Exit was
    /// requested) the close proceeds.
    /// </summary>
    public bool ShouldMinimizeToTray()
    {
        if (_exitRequested)
            return false;

        try
        {
            return _host?.Services.GetService<ISettingsService>()?.Current.MinimizeToTray ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IHost BuildHost()
    {
        return Host.CreateDefaultBuilder()
            .UseEnvironment(Microsoft.Extensions.Hosting.Environments.Production)
            .UseSerilog((context, configuration) =>
            {
                var level = context.Configuration["Serilog:MinimumLevel"] is { } raw
                            && Enum.TryParse<LogEventLevel>(raw, ignoreCase: true, out var parsed)
                    ? parsed
                    : LogEventLevel.Information;

                LogConfiguration.Configure(configuration, level);
            })
            .ConfigureServices((context, services) =>
            {
                var configuration = context.Configuration;

                var appSettings = configuration
                                      .GetSection(AppSettings.SectionName).Get<AppSettings>()
                                  ?? new AppSettings();

                var recognitionOptions = configuration
                                             .GetSection(RecognitionOptions.SectionName).Get<RecognitionOptions>()
                                         ?? new RecognitionOptions();

                // Capture size is persisted in the database (AppSettings.CameraWidth
                // / CameraHeight) but it starts life here, in appsettings.json, so
                // both documented sources stay authoritative: an explicit value in
                // the Settings section wins over the Camera section, and the
                // database wins over both. Without this the Camera section was
                // written down in the README and silently ignored.
                var cameraOptions = configuration
                                        .GetSection(CameraOptions.SectionName).Get<CameraOptions>()
                                    ?? new CameraOptions();

                var settingsSection = configuration.GetSection(AppSettings.SectionName);
                if (settingsSection[nameof(AppSettings.CameraWidth)] is null)
                    appSettings.CameraWidth = cameraOptions.Width;
                if (settingsSection[nameof(AppSettings.CameraHeight)] is null)
                    appSettings.CameraHeight = cameraOptions.Height;

                // Order matters only for readability: data -> infrastructure -> face.
                services.AddSecurityData();
                services.AddSecurityInfrastructure(appSettings, recognitionOptions);
                services.AddSecurityFace(allowModelDownload: true, cameraOptions: cameraOptions);

                // Presentation layer.
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<IUserDialogService, UserDialogService>();
                services.AddSingleton<IToastService, ToastService>();
                services.AddSingleton<CameraCoordinator>();

                // Background monitoring loop (Phase 3). Registered once and
                // reused as the hosted service so the tray and dashboard can
                // resolve the same instance.
                services.AddSingleton<SecurityMonitorService>();
                services.AddHostedService(sp => sp.GetRequiredService<SecurityMonitorService>());

                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<FaceProfileViewModel>();
                services.AddSingleton<CameraViewModel>();
                services.AddSingleton<EventsViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<AboutViewModel>();
                services.AddSingleton<MainViewModel>();

                services.AddSingleton(sp => new MainWindow
                {
                    DataContext = sp.GetRequiredService<MainViewModel>(),
                });
            })
            .Build();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            _startupCts?.Cancel();

            // Tray first: no ghost icon left in the notification area.
            try
            {
                _tray?.Dispose();
                _tray = null;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Tray icon disposal failed");
            }

            // No orphan alert window left on screen after exit.
            try
            {
                _alertWindow?.Close();
                _alertWindow = null;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Security alert window disposal failed");
            }

            if (_host is not null)
            {
                var services = _host.Services;

                // Orderly teardown: stop consuming frames before the container
                // disposes the services they depend on.
                try
                {
                    var coordinator = services.GetRequiredService<CameraCoordinator>();
                    if (coordinator.IsRunning)
                        await coordinator.StopAsync(CancellationToken.None);

                    coordinator.Dispose();
                }
                catch (Exception ex)
                {
                    services.GetService<ILogger<App>>()?.LogWarning(ex, "Camera shutdown failed");
                }

                var events = services.GetService<ISecurityEventService>();
                if (events is not null)
                    await events.RecordAsync(SecurityEventType.ApplicationStopped, SecurityEventResult.Info,
                        "Application stopped.");

                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Shutdown encountered an error");
        }
        finally
        {
            try
            {
                _instanceGuard?.Dispose();
                _instanceGuard = null;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Instance guard disposal failed");
            }

            Log.CloseAndFlush();
            _startupCts?.Dispose();
            base.OnExit(e);
        }
    }
}

/// <summary>Version labels shown in About and the title bar.</summary>
public static class VersionInfo
{
    public const string ProductName = "Security";
    public const string Phase = "Phase 3";

    public static string Version { get; } =
        (typeof(VersionInfo).Assembly.GetName().Version ?? new Version(0, 3, 0)).ToString(3);

    public static string Display => $"{ProductName} {Version} ({Phase})";
}
