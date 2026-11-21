using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

public sealed record UpdateCheckResult(bool Success, string? Error, UpdateInfo? Update, bool IsNewer);
public sealed record UpdateProgress(double Percent, string Status);
public sealed record UpdateDownloadResult(bool Success, string? Error, string? FilePath);

/// <summary>
/// Checks the public update manifest (see <see cref="AppEndpoints"/>), downloads a new
/// build, verifies its SHA-256 and replaces the running executable through a helper script.
/// Credentials are never stored in the client: the site serves the build over HTTPS.
/// </summary>
public sealed class UpdateService
{
    public static string ManifestUrl => AppEndpoints.ManifestUrl;
    public static string SiteUrl => AppEndpoints.SiteUrl;

    private readonly LogService _log;
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);

    public UpdateService(LogService log) => _log = log;

    /// <summary>Raised once per session when a newer build is discovered.</summary>
    public event Action<UpdateInfo>? UpdateFound;

    /// <summary>Raised after the installer helper has started; the app must exit now.</summary>
    public event Action? RestartRequested;

    public string UpdatesDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RDG.Zapret",
            "updates");

    public static string CurrentVersionText
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (!AppEndpoints.IsConfigured)
            return new UpdateCheckResult(false, "сервер обновлений не настроен", null, false);

        try
        {
            using var client = CreateClient(TimeSpan.FromSeconds(25));
            var url = ManifestUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            _log.Debug("update", "Проверяю обновления: " + ManifestUrl);

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(false, $"сервер ответил {(int)response.StatusCode}", null, false);

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var info = ParseManifest(json);
            if (info is null)
                return new UpdateCheckResult(false, "некорректный version.json", null, false);

            var isNewer = CompareVersions(info.Version, CurrentVersionText) > 0;
            _log.Info("update",
                isNewer
                    ? $"Доступна новая версия {info.Version} (установлена {CurrentVersionText})"
                    : $"Обновлений нет (установлена {CurrentVersionText})");

            if (isNewer && _announced.Add(info.Version))
                Ui.Invoke(() => UpdateFound?.Invoke(info));

            return new UpdateCheckResult(true, null, info, isNewer);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(false, "проверка отменена", null, false);
        }
        catch (Exception ex)
        {
            _log.Warn("update", "Не удалось проверить обновления: " + ex.Message);
            return new UpdateCheckResult(false, ex.Message, null, false);
        }
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdateInfo info,
        IProgress<UpdateProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(UpdatesDirectory);
            var destination = Path.Combine(UpdatesDirectory, "RDG.Zapret-" + SafeVersion(info.Version) + ".exe");

            if (File.Exists(destination) &&
                (string.IsNullOrWhiteSpace(info.Sha256) || await MatchesHashAsync(destination, info.Sha256, cancellationToken).ConfigureAwait(false)))
            {
                progress.Report(new UpdateProgress(100, "Файл уже скачан"));
                return new UpdateDownloadResult(true, null, destination);
            }

            progress.Report(new UpdateProgress(1, "Подключаюсь к серверу обновлений…"));
            _log.Info("update", $"Скачивание {info.Version}: {info.DownloadUrl}");

            using var client = CreateClient(TimeSpan.FromMinutes(10));
            using var response = await client
                .GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? info.Size;
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

                    var percent = total > 0 ? read * 92.0 / total : 30;
                    var status = total > 0
                        ? $"Скачиваю обновление… {FormatBytes(read)} из {FormatBytes(total)}"
                        : $"Скачиваю обновление… {FormatBytes(read)}";
                    progress.Report(new UpdateProgress(Math.Clamp(percent, 1, 92), status));
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(info.Sha256))
            {
                _log.Warn("update", "В манифесте нет SHA-256 — целостность файла не проверена");
            }
            else
            {
                progress.Report(new UpdateProgress(95, "Проверяю контрольную сумму…"));
                if (!await MatchesHashAsync(destination, info.Sha256, cancellationToken).ConfigureAwait(false))
                {
                    TryDelete(destination);
                    _log.Error("update", "SHA-256 не совпал — файл обновления удалён");
                    return new UpdateDownloadResult(false, "контрольная сумма не совпала", null);
                }
            }

            progress.Report(new UpdateProgress(100, "Обновление готово к установке"));
            _log.Success("update", $"Обновление {info.Version} скачано: {destination}");
            return new UpdateDownloadResult(true, null, destination);
        }
        catch (OperationCanceledException)
        {
            return new UpdateDownloadResult(false, "загрузка отменена", null);
        }
        catch (Exception ex)
        {
            _log.Error("update", "Ошибка загрузки обновления: " + ex);
            return new UpdateDownloadResult(false, ex.Message, null);
        }
    }

    /// <summary>Starts a detached PowerShell helper that waits for this process, swaps the exe and relaunches it.</summary>
    public bool TryStartInstaller(string downloadedFile, string version, out string? error)
    {
        error = null;

        try
        {
            if (!File.Exists(downloadedFile))
            {
                error = "файл обновления не найден";
                return false;
            }

            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) ||
                !currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                error = "не удалось определить путь к приложению";
                return false;
            }

            var directory = Path.GetDirectoryName(currentExe);
            if (string.IsNullOrWhiteSpace(directory))
            {
                error = "не удалось определить папку приложения";
                return false;
            }

            if (!IsDirectoryWritable(directory, out var writeError))
            {
                error = "нет прав на запись в папку приложения" +
                        (string.IsNullOrWhiteSpace(writeError) ? string.Empty : ": " + writeError);
                return false;
            }

            var scriptPath = Path.Combine(Path.GetTempPath(), "RDG.Zapret-update-" + Guid.NewGuid().ToString("N") + ".ps1");
            var logPath = Path.Combine(Path.GetTempPath(), "RDG.Zapret-update.log");
            var pid = Environment.ProcessId;

            var script = BuildInstallerScript(pid, downloadedFile, currentExe, logPath);
            File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "не удалось запустить установщик";
                return false;
            }

            _log.Info("update", $"Установщик {version} запущен, ожидаю завершения приложения");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("update", "Не удалось запустить установщик: " + ex);
            error = ex.Message;
            return false;
        }
    }

    public void RequestRestart() => Ui.Invoke(() => RestartRequested?.Invoke());

    private static string BuildInstallerScript(int pid, string source, string destination, string logPath)
    {
        static string Q(string value) => "'" + value.Replace("'", "''") + "'";

        var builder = new StringBuilder();
        builder.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        builder.AppendLine("$processId = " + pid);
        builder.AppendLine("$src = " + Q(source));
        builder.AppendLine("$dst = " + Q(destination));
        builder.AppendLine("$log = " + Q(logPath));
        builder.AppendLine("\"start $(Get-Date -Format o) pid=$processId\" | Set-Content -LiteralPath $log -Encoding UTF8");
        builder.AppendLine("for ($i = 0; $i -lt 240; $i++) {");
        builder.AppendLine("    if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) { break }");
        builder.AppendLine("    Start-Sleep -Milliseconds 500");
        builder.AppendLine("}");
        builder.AppendLine("Start-Sleep -Milliseconds 700");
        builder.AppendLine("try { Copy-Item -LiteralPath $dst -Destination ($dst + '.bak') -Force -ErrorAction Stop } catch { }");
        builder.AppendLine("$copied = $false");
        builder.AppendLine("for ($i = 0; $i -lt 120; $i++) {");
        builder.AppendLine("    try { Copy-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop; $copied = $true; break } catch { Start-Sleep -Milliseconds 500 }");
        builder.AppendLine("}");
        builder.AppendLine("Add-Content -LiteralPath $log -Value \"copied=$copied\"");
        builder.AppendLine("if ($copied) { Remove-Item -LiteralPath $src -Force }");
        builder.AppendLine("Start-Process -FilePath $dst");
        builder.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force");
        return builder.ToString();
    }

    private static UpdateInfo? ParseManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        var root = document.RootElement;

        var version = GetString(root, "version") ?? GetString(root, "latest") ?? GetString(root, "tag");
        if (string.IsNullOrWhiteSpace(version))
            return null;

        version = version.Trim().TrimStart('v', 'V');

        var url = GetString(root, "url")
                  ?? GetString(root, "download_url")
                  ?? GetString(root, "download")
                  ?? "RDG.Zapret.exe";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            if (Uri.TryCreate(new Uri(ManifestUrl), url, out var relative))
                absolute = relative;
            else
                return null;
        }

        var sha = (GetString(root, "sha256") ?? GetString(root, "sha") ?? GetString(root, "checksum") ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

        var size = GetLong(root, "size") ?? GetLong(root, "length") ?? 0;
        var notes = GetString(root, "notes") ?? GetString(root, "changelog") ?? string.Empty;
        var published = GetString(root, "published") ?? GetString(root, "published_at") ?? string.Empty;

        return new UpdateInfo(version, absolute.ToString(), sha, size, notes.Trim(), published.Trim());
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static async Task<bool> MatchesHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            return string.Equals(actual, expected.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static int CompareVersions(string left, string right)
    {
        if (Version.TryParse(left, out var a) && Version.TryParse(right, out var b))
        {
            static Version Normalize(Version v) =>
                new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
            return Normalize(a).CompareTo(Normalize(b));
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectoryWritable(string directory, out string? error)
    {
        error = null;
        try
        {
            var probe = Path.Combine(directory, ".rdg-update-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string SafeVersion(string version)
    {
        var builder = new StringBuilder(version.Length);
        foreach (var c in version)
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
                builder.Append(c);
        }

        return builder.Length == 0 ? "latest" : builder.ToString();
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RDG.Zapret/" + CurrentVersionText);
        client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        return client;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / 1024.0 / 1024.0:0.0} МБ";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:0} КБ";
        return $"{bytes} Б";
    }
}
