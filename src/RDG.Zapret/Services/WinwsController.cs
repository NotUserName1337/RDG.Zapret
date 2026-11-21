using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

public sealed class WinwsController : IDisposable
{
    private readonly LogService _log;
    private readonly object _sync = new();
    private Process? _process;
    private int _processId;
    private DateTime? _startedAt;
    private bool _disposed;

    public WinwsController(LogService log) => _log = log;

    public event EventHandler? StateChanged;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                try
                {
                    return _process is { HasExited: false };
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    public StrategyPreset? ActivePreset { get; private set; }
    public bool IsExternal { get; private set; }
    public int? ProcessId { get; private set; }

    public DateTime? StartedAt
    {
        get
        {
            lock (_sync)
                return _startedAt;
        }
    }

    public TimeSpan Uptime
    {
        get
        {
            var started = StartedAt;
            return started is null ? TimeSpan.Zero : DateTime.Now - started.Value;
        }
    }

    public async Task<StartOutcome> StartAsync(StrategyPreset preset, CancellationToken cancellationToken = default)
    {
        await StopAsync(quiet: true).ConfigureAwait(true);

        if (!File.Exists(preset.EnginePath))
            return StartOutcome.Fail($"Не найден движок {preset.EnginePath}");

        var stderr = new Queue<string>();
        Process process;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = preset.EnginePath,
                Arguments = preset.Arguments,
                WorkingDirectory = Directory.Exists(preset.WorkingDirectory)
                    ? preset.WorkingDirectory
                    : Path.GetDirectoryName(preset.EnginePath) ?? preset.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => HandleOutput(e.Data, false);
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                    return;
                lock (stderr)
                {
                    stderr.Enqueue(e.Data.Trim());
                    while (stderr.Count > 10)
                        stderr.Dequeue();
                }

                HandleOutput(e.Data, true);
            };

            if (!process.Start())
                return StartOutcome.Fail("Процесс winws не запустился");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            var message = ex is Win32Exception { NativeErrorCode: 740 }
                ? "winws.exe требует прав администратора. Перезапустите RDG.Zapret от имени администратора."
                : ex.Message;
            _log.Error("zapret", $"Не удалось запустить winws: {message}");
            return StartOutcome.Fail(message);
        }

        lock (_sync)
        {
            _process = process;
            _processId = process.Id;
        }

        _log.Info("zapret", $"Запуск «{preset.DisplayName}» · {preset.EngineName} · PID {process.Id}");
        _log.Debug("zapret", "Аргументы: " + Trim(preset.Arguments, 900));

        // Give WinDivert time to load (or winws time to fail on a bad strategy).
        try
        {
            await Task.Delay(1200, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(quiet: true).ConfigureAwait(true);
            throw;
        }

        bool exited;
        try
        {
            exited = process.HasExited;
        }
        catch
        {
            exited = true;
        }

        if (exited)
        {
            int exitCode;
            try
            {
                exitCode = process.ExitCode;
            }
            catch
            {
                exitCode = -1;
            }

            string details;
            lock (stderr)
                details = string.Join(" · ", stderr.TakeLast(3));

            ResetProcess(process);
            process.Dispose();

            var message = $"winws завершился (код {exitCode})" + (string.IsNullOrWhiteSpace(details) ? string.Empty : $": {details}");
            _log.Error("zapret", message);
            return StartOutcome.Fail(message);
        }

        ActivePreset = preset;
        IsExternal = false;
        ProcessId = process.Id;
        lock (_sync)
            _startedAt = DateTime.Now;

        RaiseStateChanged();
        return StartOutcome.Ok();
    }

    public async Task StopAsync(bool quiet = false)
    {
        Process? process;
        int knownPid = 0;

        lock (_sync)
        {
            process = _process;
            knownPid = _processId;
            _process = null;
            _processId = 0;
            _startedAt = null;
        }

        var wasRunning = process is not null || ActivePreset is not null || IsExternal;
        ActivePreset = null;
        IsExternal = false;
        ProcessId = null;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    if (!quiet)
                        _log.Info("zapret", $"Останавливаю winws (PID {process.Id})…");
                    process.Kill(entireProcessTree: true);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("zapret", $"Не удалось корректно остановить winws: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        var killedExtras = new List<int>();
        foreach (var name in new[] { "winws", "winws2" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var candidate in processes)
            {
                try
                {
                    if (candidate.Id == knownPid || candidate.HasExited)
                        continue;

                    candidate.Kill(entireProcessTree: true);
                    killedExtras.Add(candidate.Id);
                }
                catch
                {
                    // process may have exited or be protected
                }
                finally
                {
                    candidate.Dispose();
                }
            }
        }

        if (killedExtras.Count > 0)
        {
            _log.Warn("zapret", $"Остановлены сторонние процессы winws: {string.Join(", ", killedExtras)}");
        }

        if (wasRunning || killedExtras.Count > 0)
        {
            if (!quiet)
                _log.Info("zapret", "Обход остановлен");
            RaiseStateChanged();
        }
    }

    public bool TryAdoptExisting()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("winws")
                .Concat(Process.GetProcessesByName("winws2"))
                .OrderBy(p => p.Id)
                .ToArray();
        }
        catch
        {
            return false;
        }

        var alive = new List<Process>();
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                    alive.Add(process);
                else
                    process.Dispose();
            }
            catch
            {
                process.Dispose();
            }
        }

        if (alive.Count == 0)
            return false;

        for (var i = 1; i < alive.Count; i++)
        {
            try
            {
                alive[i].Kill(entireProcessTree: true);
                _log.Warn("zapret", $"Найдено несколько winws, лишний процесс остановлен (PID {alive[i].Id})");
            }
            catch
            {
                // ignored
            }
            finally
            {
                alive[i].Dispose();
            }
        }

        var chosen = alive[0];
        try
        {
            chosen.EnableRaisingEvents = true;
            chosen.Exited += (_, _) => HandleUnexpectedExit(chosen);

            lock (_sync)
            {
                _process = chosen;
                _processId = chosen.Id;
                _startedAt = null;
                try
                {
                    _startedAt = chosen.StartTime;
                }
                catch
                {
                    _startedAt = DateTime.Now;
                }
            }

            IsExternal = true;
            ActivePreset = null;
            ProcessId = chosen.Id;
            _log.Info("zapret", $"Обнаружен уже запущенный winws.exe (PID {chosen.Id}). Управление перехвачено.");
            RaiseStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("zapret", $"Не удалось подключиться к запущенному winws: {ex.Message}");
            chosen.Dispose();
            return false;
        }
    }

    private void HandleOutput(string? line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var text = line.Trim();
        if (text.Length > 500)
            text = text[..500] + "…";

        if (isError)
            _log.Warn("winws", text);
        else
            _log.Debug("winws", text);
    }

    private void HandleUnexpectedExit(Process process)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_process, process))
                return;
            _process = null;
            _processId = 0;
            _startedAt = null;
        }

        ActivePreset = null;
        IsExternal = false;
        ProcessId = null;
        _log.Error("zapret", $"winws неожиданно завершился (PID {process.Id})");
        RaiseStateChanged();
    }

    private void ResetProcess(Process process)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _processId = 0;
                _startedAt = null;
            }
        }

        ActivePreset = null;
        IsExternal = false;
        ProcessId = null;
    }

    private void RaiseStateChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => StateChanged?.Invoke(this, EventArgs.Empty)));
            return;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public void Detach()
    {
        lock (_sync)
        {
            if (_process is null)
                return;

            try
            {
                _process.EnableRaisingEvents = false;
            }
            catch
            {
                // ignored
            }

            _process.Dispose();
            _process = null;
            _processId = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Detach();
    }
}
