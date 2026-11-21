using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

/// <summary>
/// Downloads a strategy archive from the update host and installs only new or
/// changed .bat files. Classic winws strategies go to the zapret root; winws2
/// strategies are routed to zapret2 and renamed to the kinlay0 convention.
/// </summary>
public sealed class StrategyPackService
{
    public static string ReleasesUrl => AppEndpoints.ReleasesUrl;
    public static string VersionUrl => AppEndpoints.ManifestUrl;
    public static string DefaultPackUrl => AppEndpoints.DefaultPackUrl;

    private const long MaxArchiveBytes = 30 * 1024 * 1024;
    private const int MaxStrategyFiles = 600;

    private static readonly Regex Winws2Regex = new(
        "\\bwinws2\\.exe\\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WinwsRegex = new(
        "\\bwinws\\.exe\\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex KinlayNameRegex = new(
        "^general \\((?<variant>.+)\\)\\.bat$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex KnownClassicNameRegex = new(
        "^(general.*|\\d+)\\.bat$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly LogService _log;
    private readonly SettingsService _settings;

    public StrategyPackService(LogService log, SettingsService settings)
    {
        _log = log;
        _settings = settings;
    }

    public string StatePath => Path.Combine(_settings.DirectoryPath, "strategy-pack.json");
    public string BackupRoot => Path.Combine(_settings.DirectoryPath, "strategy-backups");

    public async Task<StrategyPackResult> CheckAndInstallAsync(
        string root,
        bool manual,
        IProgress<StrategyPackProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new StrategyPackResult { Success = false, Error = "Папка zapret не найдена." };

        try
        {
            progress?.Report(new StrategyPackProgress(2, "Ищу обновление стратегий…"));
            var source = await ResolveSourceAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(source.Url))
                return new StrategyPackResult
                {
                    Success = false,
                    Error = AppEndpoints.IsConfigured
                        ? "Не удалось найти архив стратегий на сервере."
                        : "Сервер обновлений не настроен. Укажите URL пакета стратегий в настройках или задайте update-endpoints.json."
                };

            var workDir = Path.Combine(Path.GetTempPath(), "RDG.Zapret-strategies-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            var zipPath = Path.Combine(workDir, "strategies.zip");

            try
            {
                progress?.Report(new StrategyPackProgress(8, "Скачиваю архив стратегий…"));
                var download = await DownloadAsync(source.Url, zipPath, progress, cancellationToken).ConfigureAwait(false);
                if (!download.Success &&
                    source.Url is not null &&
                    !source.Url.Equals(DefaultPackUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Debug("strategies", "Основной адрес недоступен, пробую strategies.zip");
                    download = await DownloadAsync(DefaultPackUrl, zipPath, progress, cancellationToken).ConfigureAwait(false);
                    if (download.Success)
                        source = source with { Url = DefaultPackUrl, Sha256 = null };
                }

                if (!download.Success)
                    return new StrategyPackResult { Success = false, Error = download.Error };

                var hash = await Sha256FileAsync(zipPath, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(source.Sha256) &&
                    !hash.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new StrategyPackResult
                    {
                        Success = false,
                        Error = "SHA-256 архива стратегий не совпал с манифестом."
                    };
                }

                var state = LoadState();
                var z2Ready = File.Exists(Path.Combine(root, "zapret2", "bin", "winws2.exe"));
                var hasKinlayInstalled = state.Files.Any(f => f.Engine == "zapret2");
                if (!manual &&
                    hash.Equals(state.Hash, StringComparison.OrdinalIgnoreCase) &&
                    hash.Equals(_settings.Current.StrategyPackHash, StringComparison.OrdinalIgnoreCase) &&
                    (!z2Ready || hasKinlayInstalled))
                {
                    progress?.Report(new StrategyPackProgress(100, "Стратегии уже актуальны"));
                    return new StrategyPackResult
                    {
                        Success = true,
                        Version = state.Version,
                        Hash = hash,
                        Skipped = state.Files.Count
                    };
                }

                // Keep the settings hash in sync even when the state file was
                // restored from a previous run.
                if (!manual && hash.Equals(state.Hash, StringComparison.OrdinalIgnoreCase) &&
                    !hash.Equals(_settings.Current.StrategyPackHash, StringComparison.OrdinalIgnoreCase) &&
                    (!z2Ready || hasKinlayInstalled))
                {
                    _settings.Update(s =>
                    {
                        s.StrategyPackHash = hash;
                        s.StrategyPackVersion = state.Version;
                        s.LastStrategyPackCheckDate = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
                    }, notify: false);
                    progress?.Report(new StrategyPackProgress(100, "Стратегии уже актуальны"));
                    return new StrategyPackResult
                    {
                        Success = true,
                        Version = state.Version,
                        Hash = hash,
                        Skipped = state.Files.Count
                    };
                }

                progress?.Report(new StrategyPackProgress(35, "Проверяю и устанавливаю стратегии…"));
                var result = await InstallFromArchiveAsync(
                    root, zipPath, source.Version, hash, progress, cancellationToken).ConfigureAwait(false);

                if (result.Success && result.HasChanges)
                {
                    _settings.Update(s =>
                    {
                        s.StrategyPackHash = hash;
                        s.StrategyPackVersion = result.Version ?? source.Version ?? string.Empty;
                        s.LastStrategyPackCheckDate = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
                    });
                }
                else if (result.Success)
                {
                    _settings.Update(s =>
                    {
                        s.StrategyPackHash = hash;
                        s.StrategyPackVersion = result.Version ?? source.Version ?? string.Empty;
                        s.LastStrategyPackCheckDate = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
                    }, notify: false);
                }

                return result;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* ignored */ }
            }
        }
        catch (OperationCanceledException)
        {
            return new StrategyPackResult { Success = false, Error = "Проверка стратегий отменена." };
        }
        catch (Exception ex)
        {
            _log.Warn("strategies", "Не удалось обновить стратегии: " + ex.Message);
            return new StrategyPackResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>Install a local archive. Public for diagnostics and tests.</summary>
    public async Task<StrategyPackResult> InstallFromArchiveAsync(
        string root,
        string zipPath,
        string? version,
        string? packHash,
        IProgress<StrategyPackProgress>? progress,
        CancellationToken cancellationToken)
    {
        var backupDir = CreateBackupDirectory();
        var state = LoadState();
        var installed = new List<InstalledStrategyFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newNames = new List<string>();
        var warnings = new List<string>();
        var added = 0;
        var updated = 0;
        var skipped = 0;
        var processed = 0;
        var z2 = Path.Combine(root, "zapret2");
        var z2Ready = File.Exists(Path.Combine(z2, "bin", "winws2.exe"));

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                continue;

            // The upload is a flat set of .bat files. Nested paths are not
            // trusted: they could escape the target folder.
            if (entry.FullName.Contains('/') || entry.FullName.Contains('\\'))
            {
                warnings.Add($"{entry.FullName}: вложенные папки пропущены");
                skipped++;
                continue;
            }

            var extension = Path.GetExtension(entry.FullName);
            if (!extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.Length > 2 * 1024 * 1024)
            {
                warnings.Add($"{entry.FullName}: файл слишком большой");
                skipped++;
                continue;
            }

            if (++processed > MaxStrategyFiles)
            {
                warnings.Add("Слишком много файлов в архиве — остальные пропущены");
                break;
            }

            string content;
            await using (var stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            var engine = Winws2Regex.IsMatch(content)
                ? "zapret2"
                : WinwsRegex.IsMatch(content) ? "zapret" : string.Empty;

            if (engine.Length == 0)
            {
                warnings.Add($"{entry.FullName}: не найдена команда winws / winws2");
                skipped++;
                continue;
            }

            if (engine == "zapret2" && !z2Ready)
            {
                warnings.Add($"{entry.FullName}: zapret2 не установлен");
                skipped++;
                continue;
            }

            var targetDir = engine == "zapret2" ? z2 : root;
            var targetName = engine == "zapret2" ? KinlayName(entry.FullName) : entry.FullName;
            targetName = SanitizeName(targetName);
            if (targetName.Length == 0)
            {
                skipped++;
                continue;
            }

            if (engine == "zapret2")
                content = RewriteWinws2Paths(content);

            if (!seen.Add(engine + "|" + targetName))
            {
                skipped++;
                continue;
            }

            var bytes = new UTF8Encoding(false).GetBytes(content);
            var hash = Sha256Bytes(bytes);
            var targetPath = Path.Combine(targetDir, targetName);
            var existed = File.Exists(targetPath);

            if (existed && IsSameFile(targetPath, hash))
            {
                installed.Add(new InstalledStrategyFile { Engine = engine, Name = targetName, Sha256 = hash });
                skipped++;
                continue;
            }

            if (existed && !IsKnownName(targetName) &&
                !state.Files.Any(f => f.Engine == engine && f.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase)))
            {
                targetName = UniqueName(targetDir, engine == "zapret2" ? "community " + targetName : "RDG " + targetName);
                targetPath = Path.Combine(targetDir, targetName);
                existed = File.Exists(targetPath);
            }

            if (existed)
                BackupFile(targetPath, backupDir, engine, targetName);

            try
            {
                await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                warnings.Add($"{entry.FullName}: не удалось записать — {ex.Message}");
                RestoreOrDelete(targetPath, backupDir, engine, targetName, existed);
                skipped++;
                continue;
            }

            var parsed = PresetParser.Parse(targetPath);
            if (parsed.Preset is null)
            {
                warnings.Add($"{entry.FullName}: не разобралась ({parsed.Error})");
                RestoreOrDelete(targetPath, backupDir, engine, targetName, existed);
                skipped++;
                continue;
            }

            installed.Add(new InstalledStrategyFile { Engine = engine, Name = targetName, Sha256 = hash });
            newNames.Add(parsed.Preset.DisplayName);
            if (existed) updated++; else added++;

            var current = processed;
            progress?.Report(new StrategyPackProgress(
                Math.Clamp(35 + current * 60.0 / Math.Max(1, archive.Entries.Count), 35, 95),
                $"Установлено: {added + updated} · {parsed.Preset.DisplayName}"));
        }

        if (processed == 0)
        {
            return new StrategyPackResult
            {
                Success = false,
                Error = "В архиве не найдено ни одного .bat-файла со стратегией.",
                Warnings = warnings
            };
        }

        var removed = CleanupLegacyRootWinws2(root, z2, backupDir, z2Ready);

        var result = new StrategyPackResult
        {
            Success = true,
            Added = added,
            Updated = updated,
            Skipped = skipped,
            RemovedDuplicates = removed,
            Version = version,
            Hash = packHash,
            NewStrategies = newNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Warnings = warnings
        };

        state.Version = version ?? string.Empty;
        state.Hash = packHash ?? string.Empty;
        state.InstalledAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
        state.Files = installed;
        SaveState(state);
        PruneBackups();

        _log.Info("strategies",
            $"Пакет стратегий: +{added}, обновлено {updated}, пропущено {skipped}, дублей убрано {removed}");
        foreach (var warning in warnings.Take(10))
            _log.Debug("strategies", warning);

        progress?.Report(new StrategyPackProgress(100, result.Summary));
        return result;
    }

    private async Task<PackSource> ResolveSourceAsync(CancellationToken cancellationToken)
    {
        // Without a configured update host the local strategy set is used as-is.
        if (!AppEndpoints.IsConfigured &&
            string.IsNullOrWhiteSpace(_settings.Current.StrategyPackUrl))
            return new PackSource(string.Empty, null, null);

        using var client = CreateClient(TimeSpan.FromSeconds(15));

        // 1. Explicit override from settings.
        var configured = _settings.Current.StrategyPackUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            return new PackSource(configured, null, null);

        // 2. The admin panel stores the optional strategy zip as "bundle"
        //    for the current release in releases.json.
        try
        {
            var json = await client.GetStringAsync(
                ReleasesUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var current = GetString(root, "current");
            string? bundle = null;
            string? firstBundle = null;
            string? firstVersion = null;
            string? version = current;

            if (root.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in versions.EnumerateArray())
                {
                    var itemVersion = GetString(item, "version");
                    var itemBundle = GetString(item, "bundle");

                    if (string.IsNullOrWhiteSpace(firstBundle) && !string.IsNullOrWhiteSpace(itemBundle))
                    {
                        firstBundle = itemBundle;
                        firstVersion = itemVersion;
                    }

                    if (current is not null &&
                        string.Equals(itemVersion, current, StringComparison.OrdinalIgnoreCase))
                    {
                        bundle = itemBundle;
                        version = itemVersion;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(bundle))
            {
                bundle = firstBundle;
                version ??= firstVersion;
            }

            if (!string.IsNullOrWhiteSpace(bundle))
            {
                return new PackSource(AppEndpoints.ResolveBundle(bundle), null, version);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug("strategies", "releases.json недоступен: " + ex.Message);
        }

        // 3. Optional field in version.json (future-proof).
        try
        {
            var json = await client.GetStringAsync(
                VersionUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var version = GetString(root, "version");
            var url = GetString(root, "strategies_url") ?? GetString(root, "strategies");
            var sha = GetString(root, "strategies_sha256");
            if (!string.IsNullOrWhiteSpace(url))
                return new PackSource(url, sha, version);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug("strategies", "version.json недоступен: " + ex.Message);
        }

        return new PackSource(DefaultPackUrl, null, null);
    }

    private static async Task<DownloadOutcome> DownloadAsync(
        string url,
        string destination,
        IProgress<StrategyPackProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(TimeSpan.FromMinutes(3));
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new DownloadOutcome(false, "Архив стратегий ещё не загружен на сервер.");

            if (!response.IsSuccessStatusCode)
                return new DownloadOutcome(false, $"Сервер ответил {(int)response.StatusCode}.");

            var total = response.Content.Headers.ContentLength ?? 0;
            if (total > MaxArchiveBytes)
                return new DownloadOutcome(false, "Архив стратегий слишком большой.");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var target = File.Create(destination))
            {
                var buffer = new byte[81920];
                long read = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    read += count;
                    if (read > MaxArchiveBytes)
                        return new DownloadOutcome(false, "Архив стратегий слишком большой.");

                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    var percent = total > 0 ? 8 + read * 25.0 / total : 20;
                    progress?.Report(new StrategyPackProgress(
                        Math.Clamp(percent, 8, 33),
                        total > 0 ? $"Скачиваю стратегии… {read / 1024} из {total / 1024} КБ" : "Скачиваю стратегии…"));
                }
            }

            return new DownloadOutcome(true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DownloadOutcome(false, "Не удалось скачать архив: " + ex.Message);
        }
    }

    private int CleanupLegacyRootWinws2(string root, string z2, string backupDir, bool z2Ready)
    {
        if (!z2Ready)
            return 0;

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.bat", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var content = File.ReadAllText(file, Encoding.UTF8);
                if (!Winws2Regex.IsMatch(content))
                    continue;

                var name = Path.GetFileName(file);
                var kinlay = KinlayName(name);
                var candidate = Path.Combine(z2, kinlay);
                if (!File.Exists(candidate))
                    candidate = Path.Combine(z2, name);
                if (!File.Exists(candidate))
                    continue;

                BackupFile(file, backupDir, "legacy-root", name);
                File.Delete(file);
                removed++;
                _log.Info("strategies", "Убран дубль из корня: " + name);
            }
            catch
            {
                // a locked or unreadable file is not fatal
            }
        }

        return removed;
    }

    private static string KinlayName(string name)
    {
        var match = KinlayNameRegex.Match(name);
        if (match.Success)
            return "kinlay0 (" + match.Groups["variant"].Value + ").bat";
        return name;
    }

    private static string RewriteWinws2Paths(string content) => content
        .Replace("set \"LISTS=..\\lists\\\"", "set \"LISTS=%~dp0lists\\\"", StringComparison.OrdinalIgnoreCase)
        .Replace("set \"LUA=..\\lua\\\"", "set \"LUA=%~dp0lua\\\"", StringComparison.OrdinalIgnoreCase)
        .Replace("..\\lists\\", "%~dp0lists\\", StringComparison.OrdinalIgnoreCase)
        .Replace("..\\lua\\", "%~dp0lua\\", StringComparison.OrdinalIgnoreCase)
        .Replace("../lists/", "%~dp0lists/", StringComparison.OrdinalIgnoreCase)
        .Replace("../lua/", "%~dp0lua/", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeName(string name)
    {
        name = Path.GetFileName(name);
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        name = name.Trim();
        if (name.Length == 0)
            return string.Empty;
        if (!name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            name += ".bat";
        return name;
    }

    private static bool IsKnownName(string name) =>
        KnownClassicNameRegex.IsMatch(name) ||
        name.StartsWith("kinlay0", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("community ", StringComparison.OrdinalIgnoreCase);

    private static string UniqueName(string directory, string name)
    {
        var candidate = name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var index = 2;
        while (File.Exists(Path.Combine(directory, candidate)))
            candidate = $"{stem} {index++}{ext}";
        return candidate;
    }

    private static bool IsSameFile(string path, string expectedHash)
    {
        try
        {
            return Sha256Bytes(File.ReadAllBytes(path))
                .Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void BackupFile(string source, string backupDir, string engine, string name)
    {
        try
        {
            var folder = Path.Combine(backupDir, engine);
            Directory.CreateDirectory(folder);
            File.Copy(source, Path.Combine(folder, name), overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Debug("strategies", "Не удалось сделать бэкап " + name + ": " + ex.Message);
        }
    }

    private void RestoreOrDelete(string targetPath, string backupDir, string engine, string name, bool existed)
    {
        try
        {
            if (existed)
            {
                var backup = Path.Combine(backupDir, engine, name);
                if (File.Exists(backup))
                    File.Copy(backup, targetPath, overwrite: true);
            }
            else
            {
                File.Delete(targetPath);
            }
        }
        catch
        {
            // ignored
        }
    }

    private string CreateBackupDirectory()
    {
        var path = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(path);
        return path;
    }

    private void PruneBackups()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(BackupRoot)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc)
                         .Skip(5))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* ignored */ }
            }
        }
        catch
        {
            // ignored
        }
    }

    private StrategyPackState LoadState()
    {
        try
        {
            if (!File.Exists(StatePath))
                return new StrategyPackState();
            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<StrategyPackState>(json,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new StrategyPackState();
        }
        catch
        {
            return new StrategyPackState();
        }
    }

    private void SaveState(StrategyPackState state)
    {
        try
        {
            Directory.CreateDirectory(_settings.DirectoryPath);
            File.WriteAllText(StatePath,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _log.Debug("strategies", "Не удалось сохранить состояние пакета: " + ex.Message);
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RDG.Zapret/" + UpdateService.CurrentVersionText);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Sha256Bytes(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private sealed record PackSource(string? Url, string? Sha256, string? Version);
    private sealed record DownloadOutcome(bool Success, string? Error);
}
