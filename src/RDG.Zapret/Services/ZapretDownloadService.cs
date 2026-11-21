using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RDG.Zapret.Services;

public sealed record DownloadProgress(double Percent, string Status);

public sealed record DownloadResult(
    bool Success,
    string? Error,
    string? InstallationPath,
    string? Version);

/// <summary>
/// Downloads the latest Flowseal zapret release from GitHub and installs it into
/// an application-managed folder. User lists and utility flags are preserved.
/// </summary>
public sealed class ZapretDownloadService
{
    private const string Repo = "Flowseal/zapret-discord-youtube";
    private const string ApiLatest = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private const string PageLatest = "https://github.com/" + Repo + "/releases/latest";

    private readonly LogService _log;

    public ZapretDownloadService(LogService log) => _log = log;

    public async Task<DownloadResult> EnsureLatestAsync(
        string targetPath,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        string? workDir = null;
        try
        {
            targetPath = Path.GetFullPath(targetPath);
            Directory.CreateDirectory(targetPath);

            progress.Report(new DownloadProgress(2, "Получаю информацию о последнем релизе…"));
            var release = await FindLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (release is null)
                return new DownloadResult(false,
                    "Не удалось найти zip-архив последнего релиза на GitHub. Проверьте интернет или скачайте архив вручную.",
                    null, null);

            _log.Info("download", $"Скачивание {release.Tag}: {release.AssetName}");

            workDir = Path.Combine(Path.GetTempPath(), "RDG.Zapret-download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            var zipPath = Path.Combine(workDir, "zapret.zip");

            await DownloadFileAsync(release.AssetUrl, zipPath, progress, cancellationToken).ConfigureAwait(false);

            progress.Report(new DownloadProgress(72, "Распаковываю архив…"));
            var extractDir = Path.Combine(workDir, "extract");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            var sourceRoot = FindInstallationRoot(extractDir);
            if (sourceRoot is null)
                return new DownloadResult(false,
                    "В скачанном архиве не найден winws.exe / winws2.exe. Возможно, GitHub вернул повреждённый файл.",
                    null, release.Tag);

            progress.Report(new DownloadProgress(80, "Сохраняю пользовательские списки…"));
            var preserved = PreserveUserFiles(targetPath);

            progress.Report(new DownloadProgress(84, "Устанавливаю стратегии…"));
            CopyTree(sourceRoot, targetPath);

            RestoreUserFiles(targetPath, preserved);

            progress.Report(new DownloadProgress(100, "Готово: стратегии установлены"));
            _log.Success("download", $"Стратегии {release.Tag} установлены в {targetPath}");

            return new DownloadResult(true, null, targetPath, release.Tag);
        }
        catch (OperationCanceledException)
        {
            return new DownloadResult(false, "Загрузка отменена", null, null);
        }
        catch (Exception ex)
        {
            _log.Error("download", "Ошибка загрузки стратегий: " + ex);
            return new DownloadResult(false, "Не удалось скачать стратегии: " + ex.Message, null, null);
        }
        finally
        {
            if (workDir is not null)
            {
                try
                {
                    Directory.Delete(workDir, recursive: true);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    public static string ManagedInstallPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RDG.Zapret",
            "zapret");

    private static async Task<ReleaseAsset?> FindLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient();

        try
        {
            using var response = await client.GetAsync(ApiLatest, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var tag = root.TryGetProperty("tag_name", out var tagElement)
                    ? tagElement.GetString() ?? "latest"
                    : "latest";

                var assets = new List<(string Name, string Url)>();
                if (root.TryGetProperty("assets", out var assetsElement))
                {
                    foreach (var asset in assetsElement.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var nameElement)
                            ? nameElement.GetString()
                            : null;
                        var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                            ? urlElement.GetString()
                            : null;

                        if (!string.IsNullOrWhiteSpace(name) &&
                            !string.IsNullOrWhiteSpace(url) &&
                            name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            assets.Add((name, url));
                    }
                }

                var chosen = assets
                    .OrderBy(a => a.Name.Contains("zapret-discord-youtube", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (chosen.Name is not null)
                    return new ReleaseAsset(tag, chosen.Name, chosen.Url);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // API can be rate-limited or blocked; fall back to the HTML release page.
            System.Diagnostics.Debug.WriteLine("GitHub API failed: " + ex.Message);
        }

        try
        {
            var html = await client.GetStringAsync(PageLatest, cancellationToken).ConfigureAwait(false);
            var matches = Regex.Matches(html, "href=\"(?<url>https://github\\.com/[^\"]+/releases/download/[^\"]+\\.zip)\"",
                RegexOptions.IgnoreCase);

            var urls = matches.Select(m => m.Groups["url"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(u => u.Contains("zapret-discord-youtube", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();

            if (urls.Count > 0)
            {
                var url = urls[0];
                var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                var tagMatch = Regex.Match(url, "/releases/download/(?<tag>[^/]+)/");
                var tag = tagMatch.Success ? tagMatch.Groups["tag"].Value : "latest";
                return new ReleaseAsset(tag, fileName, url);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("GitHub page failed: " + ex.Message);
        }

        return null;
    }

    private static async Task DownloadFileAsync(
        string url,
        string destination,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long read = 0;
        int count;

        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            read += count;

            if (total > 0)
            {
                var percent = 5 + read * 62.0 / total;
                var status = $"Скачиваю стратегии… {FormatBytes(read)} из {FormatBytes(total)}";
                progress.Report(new DownloadProgress(Math.Clamp(percent, 5, 68), status));
            }
            else
            {
                progress.Report(new DownloadProgress(30, $"Скачиваю стратегии… {FormatBytes(read)}"));
            }
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RDG.Zapret/1.0 (+https://github.com/)");
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/html"));
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }

    private static string? FindInstallationRoot(string extractDir)
    {
        if (HasEngine(extractDir))
            return extractDir;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(extractDir, "*", SearchOption.AllDirectories))
            {
                if (HasEngine(directory))
                    return directory;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static bool HasEngine(string directory) =>
        File.Exists(Path.Combine(directory, "winws.exe")) ||
        File.Exists(Path.Combine(directory, "winws2.exe")) ||
        File.Exists(Path.Combine(directory, "bin", "winws.exe")) ||
        File.Exists(Path.Combine(directory, "bin", "winws2.exe"));

    private static Dictionary<string, byte[]> PreserveUserFiles(string targetPath)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(targetPath))
            return result;

        foreach (var (folder, pattern) in new[]
                 {
                     ("lists", "*-user.txt"),
                     ("utils", "*.enabled")
                 })
        {
            var dir = Path.Combine(targetPath, folder);
            if (!Directory.Exists(dir))
                continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, pattern))
                {
                    var relative = Path.GetRelativePath(targetPath, file);
                    result[relative] = File.ReadAllBytes(file);
                }
            }
            catch
            {
                // ignored; update can continue
            }
        }

        return result;
    }

    private static void RestoreUserFiles(string targetPath, IReadOnlyDictionary<string, byte[]> preserved)
    {
        foreach (var (relative, content) in preserved)
        {
            try
            {
                var destination = Path.Combine(targetPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, content);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static void CopyTree(string sourceRoot, string targetRoot)
    {
        Directory.CreateDirectory(targetRoot);

        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, directory);
            Directory.CreateDirectory(Path.Combine(targetRoot, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var destination = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / 1024.0 / 1024.0:0.0} МБ";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:0} КБ";
        return $"{bytes} Б";
    }

    private sealed record ReleaseAsset(string Tag, string AssetName, string AssetUrl);
}
