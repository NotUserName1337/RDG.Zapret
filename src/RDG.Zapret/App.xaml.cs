using System.Windows;
using System.Windows.Threading;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Services;
using RDG.Zapret.ViewModels;

namespace RDG.Zapret;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private AppServices? _services;
    private ShellViewModel? _shell;
    private TrayService? _tray;
    private MainWindow? _window;

    public bool IsExiting { get; private set; }
    public bool TrayAvailable => _tray is not null;

    public bool ShouldHideOnClose =>
        !IsExiting && _tray is not null && _services?.Settings.Current.CloseToTray == true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var autoStart = e.Args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        var startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var folderArgIndex = Array.FindIndex(e.Args, a => a.Equals("--folder", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, "RDG.Zapret_SingleInstance", out var createdNew);
        _ownsMutex = createdNew;

        if (!createdNew)
        {
            if (!autoStart && !startMinimized)
            {
                MessageBox.Show(
                    "RDG.Zapret уже запущен.\n\nНайдите окно приложения на панели задач или в трее.",
                    "RDG.Zapret",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        SessionEnding += (_, _) => IsExiting = true;

        try
        {
            _services = new AppServices();

            if (folderArgIndex >= 0 && folderArgIndex + 1 < e.Args.Length)
            {
                var folder = e.Args[folderArgIndex + 1];
                _services.Settings.Update(s => s.ZapretPath = folder, notify: false);
            }

            if (autoStart && _services.Settings.Current.MinimizeOnAutostart)
                startMinimized = true;

            _shell = new ShellViewModel(_services, autoStart, startMinimized);

            _window = new MainWindow { DataContext = _shell };
            MainWindow = _window;
            _window.Show();

            try
            {
                _tray = new TrayService(_shell, _services.Monitor, ShowMainWindow, ExitApplication);

                _services.Monitor.ServicesChecked += results => _shell?.ApplyBackgroundResults(results);
                _services.Monitor.Notification += (title, message, isError) =>
                {
                    _tray?.ShowBalloon(title, message, isError);
                    if (_window?.IsVisible == true)
                        _shell?.ShowToast(message, isError);
                };
                _services.Monitor.RecoveryChanged += (active, text) => _tray?.UpdateRecovery(active, text);
            }
            catch (Exception ex)
            {
                _services.Log.Warn("tray", "Не удалось создать иконку в трее: " + ex.Message);
                _tray = null;
            }

            _services.Updates.UpdateFound += info =>
            {
                _tray?.ShowBalloon(
                    "Доступно обновление RDG.Zapret",
                    $"Версия {info.Version}. Откройте Настройки → Обновления, чтобы установить.",
                    false);

                if (_window?.IsVisible == true)
                    _shell?.ShowToast($"Доступно обновление {info.Version}", false);
            };

            _services.Updates.RestartRequested += ExitApplication;

            if (startMinimized && _tray is not null && _services.Settings.Current.CloseToTray)
                _window.Hide();

            AsyncRelayCommand.ErrorHandler = ex =>
            {
                _services.Log.Error("ui", ex.ToString());
                _shell?.ShowToast("Ошибка: " + ex.Message, true);
            };

            _ = InitializeShellAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Не удалось запустить RDG.Zapret:\n\n" + ex.Message,
                "RDG.Zapret",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task InitializeShellAsync()
    {
        try
        {
            if (_shell is not null)
                await _shell.InitializeAsync();

            _services?.Monitor.Start();
        }
        catch (Exception ex)
        {
            _services?.Log.Error("app", "Ошибка инициализации: " + ex);
            _shell?.ShowToast("Ошибка инициализации: " + ex.Message, true);
        }
    }

    public void ShowMainWindow()
    {
        if (_window is null)
            return;

        try
        {
            _window.Show();
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
        }
        catch
        {
            // ignored
        }
    }

    public void HideToTray()
    {
        if (_window is null)
            return;

        _window.Hide();
        _tray?.ShowCloseHint();
        _services?.Log.Debug("tray", "Окно свёрнуто в трей");
    }

    public void ExitApplication()
    {
        if (IsExiting)
            return;

        IsExiting = true;
        _services?.Monitor.Stop();
        _services?.Monitor.CancelRecovery();
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _services?.Monitor.Stop();
            _services?.Monitor.CancelRecovery();
            _tray?.Dispose();
            _tray = null;
            _shell?.PrepareShutdown();
        }
        catch
        {
            // never block shutdown
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch
            {
                // ignored
            }
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log.Error("ui", e.Exception.ToString());
        _shell?.ShowToast("Неожиданная ошибка: " + e.Exception.Message, true);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _services?.Log.Error("app", "Критическая ошибка: " + ex);
    }
}
