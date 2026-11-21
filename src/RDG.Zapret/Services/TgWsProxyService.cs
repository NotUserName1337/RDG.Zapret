using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

public enum TgProxyState
{
    NotInstalled,
    Stopped,
    RunningManaged,
    RunningExternal
}

public sealed record TgProxyStatus(
    TgProxyState State,
    string Host,
    int Port,
    string ProcessName,
    string Message)
{
    public bool IsRunning =>
        State is TgProxyState.RunningManaged or TgProxyState.RunningExternal;

    public bool IsManaged => State == TgProxyState.RunningManaged;
    public bool IsExternal => State == TgProxyState.RunningExternal;
}

public sealed record TgProxyStartResult(bool Success, string? Error, TgProxyStatus Status);
public sealed record TgProxyInstallResult(bool Success, string? Error, string? Version);

/// <summary>
/// Manages Flowseal's tg-ws-proxy (TgWsProxy.exe): a local MTProto proxy that
/// carries Telegram over WebSocket. The upstream program keeps its own tray
/// icon; RDG.Zapret only prepares its portable config and starts/stops it.
/// </summary>
public sealed class TgWsProxyService
{
    private const string GitHubApi = "https://api.github.com/repos/Flowseal/tg-ws-proxy/releases/latest";
    private const string GitHubPage = "https://github.com/Flowseal/tg-ws-proxy/releases/latest";
    private static string MirrorManifest => AppEndpoints.TgProxyManifestUrl;
    private static string MirrorExe => AppEndpoints.TgProxyExeUrl;

    private static readonly Regex SecretRegex = new("^[0-9a-fA-F]{32}$", RegexOptions.Compiled);
    private static readonly Regex DownloadHrefRegex = new(
        "href=\"(?<url>https://github\\.com/Flowseal/tg-ws-proxy/releases/download/[^\"]+/TgWsProxy_windows\\.exe)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly object _sync = new();

    public TgWsProxyService(SettingsService settings, LogService log)
    {
        _settings = settings;
        _log = log;

        InstallDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RDG.Zapret",
            "tgwsproxy");
        ExePath = Path.Combine(InstallDirectory, "TgWsProxy.exe");
        DataDirectory = Path.Combine(InstallDirectory, "TgWsProxy_data");
        ConfigPath = Path.Combine(DataDirectory, "config.json");
        LogPath = Path.Combine(DataDirectory, "proxy.log");
        VersionPath = Path.Combine(InstallDirectory, "version.txt");
    }

    public string InstallDirectory { get; }
    public string ExePath { get; }
    public string DataDirectory { get; }
    public string ConfigPath { get; }
    public string LogPath { get; }
    public string VersionPath { get; }

    public bool IsInstalled => File.Exists(ExePath) && new FileInfo(ExePath).Length > 1_000_000;

    public int Port => Math.Clamp(_settings.Current.TgProxyPort, 1024, 65535);

    public string Secret
    {
        get
        {
            var secret = _settings.Current.TgProxySecret?.Trim() ?? string.Empty;
            if (SecretRegex.IsMatch(secret))
                return secret.ToLowerInvariant();

            secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _settings.Update(s => s.TgProxySecret = secret, notify: false);
            return secret;
        }
    }

    public string BuildLink() => $"tg://proxy?server=127.0.0.1&port={Port}&secret=dd{Secret}";

    public string GetInstalledVersion()
    {
        try
        {
            if (File.Exists(VersionPath))
            {
                var tag = File.ReadAllText(VersionPath).Trim();
                if (tag.Length > 0)
                    return tag;
            }

            if (IsInstalled)
            {
                var info = FileVersionInfo.GetVersionInfo(ExePath);
                if (!string.IsNullOrWhiteSpace(info.FileVersion))
                    return info.FileVersion!;
            }
        }
        catch
        {
            // ignored
        }

        return "—";
    }

    /// <summary>
    /// Reuses the port and secret from an already installed TgWsProxy so the
    /// user does not have to reconnect Telegram after switching to the managed copy.
    /// </summary>
    public void ImportExternalSettings()
    {
        try
        {
            if (SecretRegex.IsMatch(_settings.Current.TgProxySecret?.Trim() ?? string.Empty))
                return;

            var external = ReadExternalConfig();
            if (external is null)
            {
                _ = Secret;
                return;
            }

            _settings.Update(s =>
            {
                if (!SecretRegex.IsMatch(s.TgProxySecret?.Trim() ?? string.Empty) &&
                    SecretRegex.IsMatch(external.Secret ?? string.Empty))
                    s.TgProxySecret = external.Secret!.ToLowerInvariant();

                if (external.Port is >= 1024 and <= 65535)
                    s.TgProxyPort = external.Port.Value;
            }, notify: false);

            _log.Info("tgproxy", $"Импортированы настройки TgWsProxy: порт {external.Port}");
        }
        catch (Exception ex)
        {
            _log.Warn("tgproxy", "Не удалось импортировать настройки TgWsProxy: " + ex.Message);
        }
        finally
        {
            _ = Secret;
        }
    }

    public TgProxyStatus GetStatus()
    {
        var managed = FindManagedProcesses();
        if (managed.Count > 0)
        {
            return new TgProxyStatus(
                TgProxyState.RunningManaged,
                "127.0.0.1",
                Port,
                managed[0].Name,
                "Прокси запущен из RDG.Zapret");
        }

        var external = FindExternalProcess();
        if (external is not null)
        {
            return new TgProxyStatus(
                TgProxyState.RunningExternal,
                "127.0.0.1",
                Port,
                external.Name,
                "Запущен отдельный TgWsProxy — управление через его иконку в трее");
        }

        if (!IsInstalled)
        {
            return new TgProxyStatus(
                TgProxyState.NotInstalled,
                "127.0.0.1",
                Port,
                string.Empty,
                "Компонент TgWsProxy ещё не установлен");
        }

        if (IsPortListening(Port))
        {
            return new TgProxyStatus(
                TgProxyState.RunningExternal,
                "127.0.0.1",
                Port,
                "неизвестный процесс",
                $"Порт {Port} уже занят другим процессом");
        }

        return new TgProxyStatus(
            TgProxyState.Stopped,
            "127.0.0.1",
            Port,
            string.Empty,
            "Прокси выключен");
    }

    public async Task<TgProxyStartResult> StartAsync(CancellationToken cancellationToken)
    {
        var status = GetStatus();
        if (status.State == TgProxyState.RunningManaged)
            return new TgProxyStartResult(true, null, status);

        if (status.State == TgProxyState.RunningExternal)
        {
            return new TgProxyStartResult(
                false,
                $"Прокси уже запущен отдельным TgWsProxy ({status.ProcessName}). " +
                "Закройте его через иконку в трее, чтобы управлять прокси из RDG.Zapret.",
                status);
        }

        if (!IsInstalled)
        {
            var install = await EnsureInstalledAsync(null, cancellationToken).ConfigureAwait(false);
            if (!install.Success)
                return new TgProxyStartResult(false, install.Error, GetStatus());
        }

        if (IsPortListening(Port))
        {
            return new TgProxyStartResult(
                false,
                $"Порт {Port} уже занят. Укажите другой порт или закройте программу, которая его использует.",
                GetStatus());
        }

        try
        {
            WriteConfig();
            EnsureMarkers();
            TryDeleteLog();

            var info = new ProcessStartInfo
            {
                FileName = ExePath,
                WorkingDirectory = InstallDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var process = Process.Start(info);
            if (process is null)
                return new TgProxyStartResult(false, "Не удалось запустить TgWsProxy.exe", GetStatus());

            lock (_sync)
                _managedProcessIds.Add(process.Id);

            _log.Info("tgproxy", $"Запуск TgWsProxy на 127.0.0.1:{Port} (pid {process.Id})");

            for (var i = 0; i < 48; i++)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);

                if (IsPortListening(Port) && FindManagedProcesses().Count > 0)
                {
                    _log.Success("tgproxy", $"Telegram-прокси запущен на 127.0.0.1:{Port}");
                    return new TgProxyStartResult(true, null, GetStatus());
                }
            }

            var details = ReadLogTail();
            await StopAsync().ConfigureAwait(false);

            var error = $"TgWsProxy не открыл порт {Port}. " +
                        (string.IsNullOrWhiteSpace(details) ? "Проверьте журнал прокси." : details);
            _log.Warn("tgproxy", error);
            return new TgProxyStartResult(false, error, GetStatus());
        }
        catch (OperationCanceledException)
        {
            await StopAsync().ConfigureAwait(false);
            return new TgProxyStartResult(false, "Запуск отменён", GetStatus());
        }
        catch (Exception ex)
        {
            _log.Error("tgproxy", "Ошибка запуска TgWsProxy: " + ex);
            return new TgProxyStartResult(false, ex.Message, GetStatus());
        }
    }

    public async Task StopAsync()
    {
        var processes = FindManagedProcesses();
        foreach (var item in processes)
        {
            try
            {
                using var process = Process.GetProcessById(item.Id);
                if (process.HasExited)
                    continue;

                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
                _log.Info("tgproxy", $"Остановлен TgWsProxy (pid {item.Id})");
            }
            catch (ArgumentException)
            {
                // The PyInstaller parent process has already exited.
            }
            catch (Exception ex)
            {
                _log.Warn("tgproxy", $"Не удалось остановить TgWsProxy (pid {item.Id}): {ex.Message}");
            }
        }

        lock (_sync)
            _managedProcessIds.Clear();

        for (var i = 0; i < 12 && FindManagedProcesses().Count > 0; i++)
            await Task.Delay(250).ConfigureAwait(false);
    }

    public async Task<TgProxyStatus> RestartAsync(CancellationToken cancellationToken)
    {
        if (GetStatus().State == TgProxyState.RunningManaged)
        {
            await StopAsync().ConfigureAwait(false);
            await Task.Delay(600, cancellationToken).ConfigureAwait(false);
        }

        var result = await StartAsync(cancellationToken).ConfigureAwait(false);
        return result.Status;
    }

    public string RegenerateSecret()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _settings.Update(s => s.TgProxySecret = secret, notify: false);
        _log.Info("tgproxy", "Сгенерирован новый секрет MTProto");
        return secret;
    }

    public async Task<TgProxyInstallResult> EnsureInstalledAsync(
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (IsInstalled)
            return new TgProxyInstallResult(true, null, GetInstalledVersion());

        Directory.CreateDirectory(InstallDirectory);

        foreach (var candidate in BundledCandidates())
        {
            try
            {
                if (!File.Exists(candidate))
                    continue;

                var length = new FileInfo(candidate).Length;
                if (length < 1_000_000)
                    continue;

                progress?.Report(new DownloadProgress(20, "Копирую TgWsProxy из папки программы…"));
                File.Copy(candidate, ExePath, overwrite: true);
                progress?.Report(new DownloadProgress(100, "TgWsProxy установлен"));
                _log.Success("tgproxy", "TgWsProxy установлен из комплекта: " + candidate);
                return new TgProxyInstallResult(true, null, GetInstalledVersion());
            }
            catch (Exception ex)
            {
                _log.Warn("tgproxy", $"Не удалось скопировать {candidate}: {ex.Message}");
            }
        }

        var mirror = await DownloadFromMirrorAsync(progress, cancellationToken).ConfigureAwait(false);
        if (mirror.Success)
            return mirror;

        return await DownloadFromGitHubAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TgProxyInstallResult> UpdateAsync(
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (GetStatus().State == TgProxyState.RunningManaged)
            await StopAsync().ConfigureAwait(false);

        var github = await DownloadFromGitHubAsync(progress, cancellationToken).ConfigureAwait(false);
        if (github.Success)
            return github;

        var mirror = await DownloadFromMirrorAsync(progress, cancellationToken).ConfigureAwait(false);
        if (mirror.Success)
            return mirror;

        return new TgProxyInstallResult(false, github.Error ?? mirror.Error ?? "Не удалось скачать TgWsProxy", null);
    }

    public bool OpenInTelegram()
    {
        try
        {
            Process.Start(new ProcessStartInfo(BuildLink()) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("tgproxy", "Не удалось открыть Telegram по ссылке: " + ex.Message);
            return false;
        }
    }

    private IEnumerable<string> BundledCandidates()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "TgWsProxy.exe");
        yield return Path.Combine(baseDir, "tgwsproxy", "TgWsProxy.exe");
    }

    private async Task<TgProxyInstallResult> DownloadFromMirrorAsync(
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!AppEndpoints.IsConfigured)
            return new TgProxyInstallResult(false, "Зеркало обновлений не настроено", null);

        try
        {
            using var client = CreateClient(TimeSpan.FromMinutes(10));
            string? version = null;
            string? expectedHash = null;
            var size = 0L;

            progress?.Report(new DownloadProgress(3, "Получаю данные TgWsProxy с сервера обновлений…"));

            try
            {
                var json = await client.GetStringAsync(MirrorManifest, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.TryGetProperty("version", out var versionElement))
                    version = versionElement.GetString();
                if (root.TryGetProperty("sha256", out var hashElement))
                    expectedHash = hashElement.GetString();
                if (root.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize))
                    size = parsedSize;
            }
            catch (Exception ex)
            {
                _log.Debug("tgproxy", "Манифест TgWsProxy недоступен: " + ex.Message);
            }

            var target = ExePath + ".download";
            await DownloadFileAsync(client, MirrorExe, target, size, progress, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                progress?.Report(new DownloadProgress(95, "Проверяю контрольную сумму…"));
                var actual = ComputeSha256(target);
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(target);
                    return new TgProxyInstallResult(false, "SHA-256 TgWsProxy не совпал", null);
                }
            }

            File.Move(target, ExePath, overwrite: true);
            if (!string.IsNullOrWhiteSpace(version))
                File.WriteAllText(VersionPath, version);
            else if (File.Exists(VersionPath))
                File.Delete(VersionPath);

            progress?.Report(new DownloadProgress(100, "TgWsProxy установлен"));
            _log.Success("tgproxy", $"TgWsProxy скачан с сервера обновлений ({version ?? "без версии"})");
            return new TgProxyInstallResult(true, null, version ?? GetInstalledVersion());
        }
        catch (OperationCanceledException)
        {
            return new TgProxyInstallResult(false, "Загрузка отменена", null);
        }
        catch (Exception ex)
        {
            _log.Warn("tgproxy", "Не удалось скачать TgWsProxy с сервера обновлений: " + ex.Message);
            return new TgProxyInstallResult(false, ex.Message, null);
        }
    }

    private async Task<TgProxyInstallResult> DownloadFromGitHubAsync(
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(TimeSpan.FromMinutes(10));
            progress?.Report(new DownloadProgress(2, "Ищу последнюю версию TgWsProxy на GitHub…"));

            string? version = null;
            string? url = null;
            string? expectedHash = null;
            long size = 0;

            try
            {
                using var response = await client.GetAsync(GitHubApi, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;

                    if (root.TryGetProperty("tag_name", out var tagElement))
                        version = tagElement.GetString();

                    var wanted = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                        ? "TgWsProxy_windows_arm64.exe"
                        : "TgWsProxy_windows.exe";

                    if (root.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                            if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                                continue;

                            url = asset.TryGetProperty("browser_download_url", out var urlElement)
                                ? urlElement.GetString()
                                : null;

                            if (asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed))
                                size = parsed;

                            if (asset.TryGetProperty("digest", out var digestElement))
                            {
                                var digest = digestElement.GetString();
                                if (!string.IsNullOrWhiteSpace(digest) &&
                                    digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                                    expectedHash = digest.Substring("sha256:".Length);
                            }

                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Debug("tgproxy", "GitHub API недоступен: " + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                var html = await client.GetStringAsync(GitHubPage, cancellationToken).ConfigureAwait(false);
                var match = DownloadHrefRegex.Match(html);
                if (match.Success)
                {
                    url = match.Groups["url"].Value;
                    var tagMatch = Regex.Match(url, "/releases/download/(?<tag>[^/]+)/");
                    if (tagMatch.Success)
                        version = tagMatch.Groups["tag"].Value;
                }
            }

            if (string.IsNullOrWhiteSpace(url))
                return new TgProxyInstallResult(false, "Не удалось найти TgWsProxy_windows.exe на GitHub", null);

            var target = ExePath + ".download";
            await DownloadFileAsync(client, url, target, size, progress, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                progress?.Report(new DownloadProgress(95, "Проверяю контрольную сумму…"));
                var actual = ComputeSha256(target);
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(target);
                    return new TgProxyInstallResult(false, "SHA-256 TgWsProxy не совпал", null);
                }
            }

            File.Move(target, ExePath, overwrite: true);
            if (!string.IsNullOrWhiteSpace(version))
                File.WriteAllText(VersionPath, version);
            else if (File.Exists(VersionPath))
                File.Delete(VersionPath);

            progress?.Report(new DownloadProgress(100, "TgWsProxy установлен"));
            _log.Success("tgproxy", $"TgWsProxy скачан с GitHub ({version ?? "без версии"})");
            return new TgProxyInstallResult(true, null, version ?? GetInstalledVersion());
        }
        catch (OperationCanceledException)
        {
            return new TgProxyInstallResult(false, "Загрузка отменена", null);
        }
        catch (Exception ex)
        {
            _log.Error("tgproxy", "Ошибка загрузки TgWsProxy: " + ex);
            return new TgProxyInstallResult(false, ex.Message, null);
        }
    }

    private static async Task DownloadFileAsync(
        HttpClient client,
        string url,
        string destination,
        long expectedSize,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedSize;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (var target = File.Create(destination))
        {
            var buffer = new byte[81920];
            long read = 0;
            int count;

            while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                read += count;

                var percent = total > 0 ? read * 92.0 / total : 25;
                var status = total > 0
                    ? $"Скачиваю TgWsProxy… {FormatBytes(read)} из {FormatBytes(total)}"
                    : $"Скачиваю TgWsProxy… {FormatBytes(read)}";
                progress?.Report(new DownloadProgress(Math.Clamp(percent, 2, 92), status));
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void WriteConfig()
    {
        Directory.CreateDirectory(DataDirectory);

        JsonObject root = new();
        var template = FindExternalConfigPath();
        if (template is not null)
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(template)) is JsonObject external)
                    root = external;
            }
            catch (Exception ex)
            {
                _log.Debug("tgproxy", "Не удалось прочитать внешний конфиг TgWsProxy: " + ex.Message);
            }
        }

        if (root.Count == 0)
            root = CreateDefaultConfig();

        root["host"] = "127.0.0.1";
        root["port"] = Port;
        root["secret"] = Secret;
        root["check_updates"] = false;
        root["autostart"] = false;
        root["language"] = "ru";
        root["appearance"] ??= "auto";
        root["verbose"] ??= false;
        root["dc_ip"] ??= new JsonArray("2:149.154.167.220", "4:149.154.167.220");
        root["cfproxy"] ??= true;
        root["h2"] ??= true;

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(ConfigPath, root.ToJsonString(options), new UTF8Encoding(false));
    }

    private static JsonObject CreateDefaultConfig() => new()
    {
        ["host"] = "127.0.0.1",
        ["port"] = 1443,
        ["secret"] = string.Empty,
        ["dc_ip"] = new JsonArray("2:149.154.167.220", "4:149.154.167.220"),
        ["verbose"] = false,
        ["buf_kb"] = 256,
        ["pool_size"] = 4,
        ["log_max_mb"] = 5,
        ["check_updates"] = false,
        ["cfproxy"] = true,
        ["h2"] = true,
        ["cfproxy_user_domain_enabled"] = false,
        ["cfproxy_user_domain"] = new JsonArray(),
        ["cfproxy_worker_enabled"] = false,
        ["cfproxy_worker_domain"] = new JsonArray(),
        ["force_test_dc"] = false,
        ["no_secure"] = false,
        ["appearance"] = "auto",
        ["language"] = "ru",
        ["autostart"] = false
    };

    private void EnsureMarkers()
    {
        Directory.CreateDirectory(DataDirectory);
        Touch(Path.Combine(DataDirectory, ".first_run_done_mtproto"));
        Touch(Path.Combine(DataDirectory, ".ipv6_warned"));
    }

    private static void Touch(string path)
    {
        try
        {
            if (!File.Exists(path))
                File.WriteAllBytes(path, Array.Empty<byte>());
        }
        catch
        {
            // ignored
        }
    }

    private string? FindExternalConfigPath()
    {
        var external = FindExternalProcess();
        if (external?.Path is not null)
        {
            var dir = Path.GetDirectoryName(external.Path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var portable = Path.Combine(dir, "TgWsProxy_data", "config.json");
                if (File.Exists(portable))
                    return portable;
            }
        }

        var roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TgWsProxy",
            "config.json");
        return File.Exists(roaming) ? roaming : null;
    }

    private ExternalTgProxyConfig? ReadExternalConfig()
    {
        var path = FindExternalConfigPath();
        if (path is null)
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            int? port = null;
            string? secret = null;
            string? host = null;

            if (root.TryGetProperty("port", out var portElement) && portElement.TryGetInt32(out var parsedPort))
                port = parsedPort;
            if (root.TryGetProperty("secret", out var secretElement))
                secret = secretElement.GetString();
            if (root.TryGetProperty("host", out var hostElement))
                host = hostElement.GetString();

            return new ExternalTgProxyConfig(host, port, secret);
        }
        catch (Exception ex)
        {
            _log.Debug("tgproxy", "Не удалось прочитать конфиг внешнего TgWsProxy: " + ex.Message);
            return null;
        }
    }

    private List<ProcessRef> FindManagedProcesses()
    {
        var result = new List<ProcessRef>();

        foreach (var process in Process.GetProcessesByName("TgWsProxy"))
        {
            try
            {
                if (process.HasExited)
                    continue;

                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // access denied; fall back to tracked pid below
                }

                if (path is not null && string.Equals(path, ExePath, StringComparison.OrdinalIgnoreCase))
                    result.Add(new ProcessRef(process.Id, process.ProcessName, path));
            }
            catch
            {
                // ignored
            }
            finally
            {
                process.Dispose();
            }
        }

        lock (_sync)
        {
            foreach (var pid in _managedProcessIds.ToArray())
            {
                if (result.Any(r => r.Id == pid))
                    continue;

                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited)
                        result.Add(new ProcessRef(pid, process.ProcessName, ExePath));
                }
                catch
                {
                    _managedProcessIds.Remove(pid);
                }
            }
        }

        return result;
    }

    private ProcessRef? FindExternalProcess()
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.HasExited)
                    continue;

                if (process.ProcessName.IndexOf("tgwsproxy", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // ignored
                }

                if (path is not null && string.Equals(path, ExePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                return new ProcessRef(process.Id, process.ProcessName, path);
            }
            catch
            {
                // ignored
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static bool IsPortListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return false;
        }
    }

    private string ReadLogTail()
    {
        try
        {
            if (!File.Exists(LogPath))
                return string.Empty;

            var lines = File.ReadAllLines(LogPath);
            return string.Join(" ", lines.TakeLast(3)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private void TryDeleteLog()
    {
        try
        {
            if (File.Exists(LogPath))
                File.Delete(LogPath);
        }
        catch
        {
            // the previous instance may still hold the file
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // ignored
        }
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RDG.Zapret/" + UpdateService.CurrentVersionText);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.0} МБ");
        if (bytes >= 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} КБ");
        return bytes + " Б";
    }

    private readonly HashSet<int> _managedProcessIds = new();

    private sealed record ProcessRef(int Id, string Name, string? Path);

    private sealed record ExternalTgProxyConfig(string? Host, int? Port, string? Secret);
}
