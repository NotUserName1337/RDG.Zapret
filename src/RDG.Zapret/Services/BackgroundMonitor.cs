using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

/// <summary>
/// Periodic background availability checks. When a service degrades while a bypass is active,
/// it runs a background strategy recovery and reports progress through tray notifications.
/// </summary>
public sealed class BackgroundMonitor : IDisposable
{
    private readonly SettingsService _settings;
    private readonly PresetScanner _scanner;
    private readonly WinwsController _winws;
    private readonly ServiceProbe _probe;
    private readonly AutoTuner _tuner;
    private readonly StrategyGenerator _generator;
    private readonly ResultsService _results;
    private readonly OperationCoordinator _operations;
    private readonly LogService _log;

    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _recoveryCts;
    private Task? _loop;
    private bool _isChecking;
    private bool _isRecovering;
    private IReadOnlyList<ProbeResult>? _lastResults;
    private int _failureStreak;
    private DateTime _nextRecoveryAllowed = DateTime.MinValue;
    private DateTime _nextCheckAt = DateTime.MinValue;
    private TimeSpan _scheduledInterval = TimeSpan.Zero;

    public BackgroundMonitor(
        SettingsService settings,
        PresetScanner scanner,
        WinwsController winws,
        ServiceProbe probe,
        AutoTuner tuner,
        StrategyGenerator generator,
        ResultsService results,
        OperationCoordinator operations,
        LogService log)
    {
        _settings = settings;
        _scanner = scanner;
        _winws = winws;
        _probe = probe;
        _tuner = tuner;
        _generator = generator;
        _results = results;
        _operations = operations;
        _log = log;
    }

    public event Action<IReadOnlyList<ProbeResult>>? ServicesChecked;
    public event Action<string, string, bool>? Notification;
    public event Action<bool, string>? RecoveryChanged;

    public bool IsRecovering
    {
        get
        {
            lock (_sync)
                return _isRecovering;
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_loop is not null)
                return;

            _cts = new CancellationTokenSource();
            _nextCheckAt = DateTime.MinValue;
            _scheduledInterval = TimeSpan.Zero;
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }

        _log.Info("monitor", "Фоновая проверка сервисов запущена");
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_loop is null)
                return;

            _cts?.Cancel();
            _recoveryCts?.Cancel();
            _loop = null;
        }

        _log.Info("monitor", "Фоновая проверка сервисов остановлена");
    }

    public void CancelRecovery()
    {
        _recoveryCts?.Cancel();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var settings = _settings.Current;
                    if (!settings.BackgroundMonitorEnabled)
                    {
                        _nextCheckAt = DateTime.MinValue;
                        _scheduledInterval = TimeSpan.Zero;
                        continue;
                    }

                    var interval = TimeSpan.FromMinutes(Math.Clamp(settings.BackgroundCheckIntervalMinutes, 1, 120));
                    if (_scheduledInterval != interval)
                    {
                        _scheduledInterval = interval;
                        _nextCheckAt = DateTime.Now + interval;
                    }

                    if (_nextCheckAt == DateTime.MinValue)
                    {
                        _nextCheckAt = DateTime.Now + interval;
                        continue;
                    }

                    if (DateTime.Now < _nextCheckAt)
                        continue;

                    _nextCheckAt = DateTime.Now + interval;

                    if (_isChecking || _isRecovering || _operations.IsBusy)
                        continue;

                    await CheckOnceAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Warn("monitor", "Ошибка фоновой проверки: " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        finally
        {
            lock (_sync)
            {
                _loop = null;
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        _isChecking = true;
        try
        {
            var results = await _probe.CheckManyAsync(
                ServiceCatalog.All,
                new ProbeOptions
                {
                    TimeoutSeconds = _settings.Current.ProbeTimeoutSeconds,
                    ClassifyWithTls = false
                },
                cancellationToken).ConfigureAwait(false);

            var previous = _lastResults;
            _lastResults = results;

            var failed = results.Where(IsDegraded).ToList();
            var failedNames = failed
                .Select(r => ServiceCatalog.TryById(r.ServiceId)?.Name ?? r.ServiceId)
                .ToList();

            _log.Info("monitor", failed.Count == 0
                ? "Фоновая проверка: все сервисы доступны"
                : $"Фоновая проверка: проблемы с {string.Join(", ", failedNames)}");

            Ui.Invoke(() => ServicesChecked?.Invoke(results));

            if (!_winws.IsRunning)
            {
                _failureStreak = 0;
                return;
            }

            if (failed.Count == 0)
            {
                _failureStreak = 0;
                return;
            }

            // A single packet-loss spike is common. Retry only the failed
            // services a few times before touching the engine or autotuning.
            var attempts = Math.Clamp(_settings.Current.BackgroundRetryAttempts, 1, 10);
            var delay = Math.Clamp(_settings.Current.BackgroundRetryDelaySeconds, 2, 60);
            var currentResults = results.ToList();
            var currentFailed = failed.ToList();

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);

                if (!_winws.IsRunning)
                {
                    _failureStreak = 0;
                    _log.Info("monitor", "Обход остановлен во время повторных проверок");
                    return;
                }

                var targets = currentFailed
                    .Select(r => ServiceCatalog.TryById(r.ServiceId))
                    .Where(t => t is not null)
                    .Select(t => t!)
                    .ToList();

                if (targets.Count == 0)
                    break;

                var retry = await _probe.CheckManyAsync(
                    targets,
                    new ProbeOptions
                    {
                        TimeoutSeconds = _settings.Current.ProbeTimeoutSeconds,
                        ClassifyWithTls = false
                    },
                    cancellationToken).ConfigureAwait(false);

                var byId = retry.ToDictionary(r => r.ServiceId, StringComparer.OrdinalIgnoreCase);
                currentResults = currentResults
                    .Select(r => byId.TryGetValue(r.ServiceId, out var replacement) ? replacement : r)
                    .ToList();
                _lastResults = currentResults;
                Ui.Invoke(() => ServicesChecked?.Invoke(currentResults));

                currentFailed = retry.Where(IsDegraded).ToList();
                if (currentFailed.Count == 0)
                {
                    _failureStreak = 0;
                    _log.Success("monitor", $"Сервисы восстановились после повторной проверки {attempt}/{attempts}");
                    return;
                }

                _log.Info("monitor",
                    $"Повторная проверка {attempt}/{attempts}: ещё недоступны " +
                    string.Join(", ", currentFailed.Select(r => ServiceCatalog.TryById(r.ServiceId)?.Name ?? r.ServiceId)));
            }

            failed = currentFailed;
            failedNames = failed
                .Select(r => ServiceCatalog.TryById(r.ServiceId)?.Name ?? r.ServiceId)
                .ToList();
            _failureStreak++;

            if (!_settings.Current.BackgroundRecoveryEnabled)
                return;

            if (DateTime.Now < _nextRecoveryAllowed)
                return;

            // An adopted external winws has no known strategy to restore.
            if (_winws.IsExternal)
            {
                _log.Info("monitor", "Внешний winws.exe: автоматическое восстановление пропущено");
                return;
            }

            _log.Warn("monitor",
                $"После {attempts} повторных проверок сервисы всё ещё недоступны ({string.Join(", ", failedNames)})");
            var serviceNames = string.Join(", ", failedNames);
            await RunRecoveryAsync(serviceNames, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("monitor", "Ошибка проверки сервисов: " + ex.Message);
        }
        finally
        {
            _isChecking = false;
        }
    }

    private async Task RunRecoveryAsync(string failedServices, CancellationToken cancellationToken)
    {
        if (_scanner.Current is null)
            return;

        if (!_operations.TryBegin("background-recovery"))
        {
            _log.Info("monitor", "Фоновый подбор пропущен: занята другая операция");
            return;
        }

        CancellationTokenSource recoveryCts;
        lock (_sync)
        {
            _isRecovering = true;
            recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _recoveryCts = recoveryCts;
        }

        Ui.Invoke(() => RecoveryChanged?.Invoke(true, "Фоновый подбор стратегии…"));
        Notify("Фоновая проверка",
            $"Проблемы с доступностью: {failedServices}. Запускаю фоновый подбор стратегии… Отмена — в меню иконки в трее.",
            false);
        _log.Warn("monitor", $"Ухудшение доступности ({failedServices}) — запускаю фоновое восстановление");

        var previous = _winws.ActivePreset;
        var wasRunning = _winws.IsRunning;
        var success = false;
        var canceled = false;

        try
        {
            var token = recoveryCts.Token;

            // Classic core: synthesize a new strategy first. If it fails, try curated presets.
            if (_scanner.Engine == ZapretEngine.Zapret && _scanner.Current.WinwsPath is not null)
            {
                success = await TryGeneratedRecoveryAsync(token).ConfigureAwait(false);
                if (!success)
                    success = await TryPresetRecoveryAsync(token).ConfigureAwait(false);
            }
            else
            {
                success = await TryPresetRecoveryAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            _log.Info("monitor", "Фоновый подбор отменён");
        }
        catch (Exception ex)
        {
            _log.Error("monitor", "Ошибка фонового подбора: " + ex);
        }
        finally
        {
            if (!success && canceled)
            {
                await _winws.StopAsync(quiet: true).ConfigureAwait(false);
            }
            else if (!success && wasRunning && previous is not null)
            {
                var restore = await _winws.StartAsync(previous, CancellationToken.None).ConfigureAwait(false);
                if (restore.Success)
                    _log.Info("monitor", "Восстановлена предыдущая стратегия");
                else
                    _log.Warn("monitor", "Не удалось восстановить предыдущую стратегию: " + restore.Error);
            }

            _nextRecoveryAllowed = DateTime.Now + (success ? TimeSpan.FromMinutes(20) : TimeSpan.FromMinutes(45));
            _failureStreak = 0;
            _isRecovering = false;
            _operations.End("background-recovery");

            lock (_sync)
            {
                if (ReferenceEquals(_recoveryCts, recoveryCts))
                    _recoveryCts = null;
            }

            recoveryCts.Dispose();

            Ui.Invoke(() => RecoveryChanged?.Invoke(false, string.Empty));

            if (canceled)
                Notify("Фоновый подбор", "Подбор отменён. Текущая стратегия остановлена", false);
            else if (success)
                Notify("Стратегия найдена", "Фоновый подбор завершён, стратегия запущена", false);
            else
                Notify("Фоновый подбор", "Рабочая стратегия не найдена. Возвращена предыдущая стратегия", true);
        }
    }

    private async Task<bool> TryGeneratedRecoveryAsync(CancellationToken cancellationToken)
    {
        var installation = _scanner.Current;
        if (installation is null)
            return false;

        _log.Info("monitor", "Фоновый генератор: перебор параметров winws…");
        var options = new GeneratorOptions
        {
            Targets = ServiceCatalog.All,
            WarmupSeconds = Math.Clamp(_settings.Current.TuneWarmupSeconds, 2, 5),
            TimeoutSeconds = _settings.Current.ProbeTimeoutSeconds,
            MaxAttempts = 30,
            KeepBestRunning = true,
            Mode = "fast"
        };

        var report = await _generator.RunAsync(
            installation,
            options,
            new Progress<GeneratorProgress>(_ => { }),
            cancellationToken).ConfigureAwait(false);

        if (!report.AllAvailable || report.Best is null || report.SavedPreset is null)
            return false;

        await _scanner.ScanAsync(installation.RootPath).ConfigureAwait(false);

        var best = report.Best;
        _results.Record(new TuneResult
        {
            Preset = report.SavedPreset,
            Score = best.Score,
            Services = best.Services,
            Note = "Фоновый генератор",
            Duration = best.Duration
        }, manual: false);

        var now = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
        _settings.Update(s =>
        {
            s.BestPresetFile = report.SavedPreset.FileName;
            s.BestScore = best.Score;
            s.BestScoreDate = now;
            s.LastTuneDate = now;
            s.LastPresetFile = report.SavedPreset.FileName;
        }, notify: false);

        _log.Success("monitor", $"Фоновый генератор нашёл стратегию: {report.SavedPreset.DisplayName} ({best.Score:0}/100)");
        return true;
    }

    private async Task<bool> TryPresetRecoveryAsync(CancellationToken cancellationToken)
    {
        var presets = _scanner.Presets;
        if (presets.Count == 0)
            return false;

        var candidates = presets
            .Where(p => !_settings.Current.TuneExcludedPresets.Any(
                x => x.Equals(p.FileName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.FileName.Equals(_settings.Current.LastPresetFile, StringComparison.OrdinalIgnoreCase) ? 0
                : p.FileName.Equals(_settings.Current.BestPresetFile, StringComparison.OrdinalIgnoreCase) ? 1
                : 2)
            .ThenBy(p => p.FileName, NaturalStringComparer.Instance)
            .Take(30)
            .ToList();

        _log.Info("monitor", $"Фоновый автоподбор: проверяю {candidates.Count} стратегий…");
        var request = new TuneRequest
        {
            Targets = ServiceCatalog.All,
            WarmupSeconds = Math.Clamp(_settings.Current.TuneWarmupSeconds, 2, 5),
            TimeoutSeconds = _settings.Current.ProbeTimeoutSeconds,
            Passes = 1,
            SkipBaselineOk = true,
            KeepBestRunning = false,
            Mode = "quick",
            AllowEarlyExit = true,
            EarlyExitScore = 99,
            TwoStage = false
        };

        var report = await _tuner.RunAsync(
            candidates,
            request,
            new Progress<TuneUpdate>(_ => { }),
            cancellationToken).ConfigureAwait(false);

        var best = report.Best;
        if (best is null)
            return false;

        var allAvailable = best.IsStable;
        if (!allAvailable)
            return false;

        var start = await _winws.StartAsync(best.Preset, cancellationToken).ConfigureAwait(false);
        if (!start.Success)
            return false;

        _results.Record(new TuneResult
        {
            Preset = best.Preset,
            Score = best.Score,
            Services = best.Services,
            Note = "Фоновый автоподбор",
            Duration = best.Duration
        }, manual: false);

        var now = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
        _settings.Update(s =>
        {
            s.BestPresetFile = best.Preset.FileName;
            s.BestScore = best.Score;
            s.BestScoreDate = now;
            s.LastTuneDate = now;
            s.LastPresetFile = best.Preset.FileName;
        }, notify: false);

        _log.Success("monitor", $"Фоновый автоподбор выбрал: {best.Preset.DisplayName} ({best.Score:0}/100)");
        return true;
    }

    private void Notify(string title, string message, bool isError) =>
        Ui.Invoke(() => Notification?.Invoke(title, message, isError));

    /// <summary>
    /// Partial is normally a "works with limitations" state. For YouTube and
    /// Discord it means the page/API loads but video/voice is blocked, which is
    /// exactly the degradation the background monitor should react to.
    /// </summary>
    private static bool IsDegraded(ProbeResult result) =>
        !result.IsSuccess ||
        (result.Status == ProbeStatus.Partial &&
         (result.ServiceId.Equals("youtube", StringComparison.OrdinalIgnoreCase) ||
          result.ServiceId.Equals("discord", StringComparison.OrdinalIgnoreCase)));

    public void Dispose()
    {
        Stop();
        lock (_sync)
            _recoveryCts?.Dispose();
    }
}
