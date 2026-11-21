using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RDG.Zapret.Infrastructure;

namespace RDG.Zapret.Services;

public sealed record Zapret2InstallResult(bool Success, string? Error, int PresetCount, string? Version);

/// <summary>
/// Installs a Windows-ready zapret2 pack (winws2.exe + Lua presets) into
/// &lt;zapret root&gt;\zapret2, renders portable config files and creates .cmd
/// presets that RDG.Zapret can launch directly. The classic zapret files are
/// left untouched, so both engines stay selectable.
/// </summary>
public sealed class Zapret2PackService
{
    public const string FolderName = "zapret2";

    private static string MirrorManifest => AppEndpoints.Zapret2ManifestUrl;
    private static string MirrorZip => AppEndpoints.Zapret2PackUrl;
    private const string GitHubApi = "https://api.github.com/repos/Dunterbabochka/zapret2-next/releases/latest";
    private const string GitHubPage = "https://github.com/Dunterbabochka/zapret2-next/releases/latest";

    private static readonly Regex GitHubZipRegex = new(
        "href=\"(?<url>https://github\\.com/Dunterbabochka/zapret2-next/releases/download/[^\"]+/zapret2-next-[^\"]+\\.zip)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly LogService _log;

    public Zapret2PackService(LogService log) => _log = log;

    public static string FolderPath(string root) => Path.Combine(root, FolderName);

    public static string ExePath(string root) => Path.Combine(root, FolderName, "bin", "winws2.exe");

    public static bool IsInstalled(string root)
    {
        try
        {
            if (!File.Exists(ExePath(root)))
                return false;

            var folder = FolderPath(root);
            return Directory.Exists(folder) &&
                   Directory.EnumerateFiles(folder, "*.cmd", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
    }

    public static string? GetInstalledVersion(string root)
    {
        try
        {
            var path = Path.Combine(FolderPath(root), "version.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<Zapret2InstallResult> InstallAsync(
        string root,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? workDir = null;

        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return new Zapret2InstallResult(false, "Папка zapret не найдена. Укажите её в Настройках.", 0, null);

            var target = FolderPath(root);
            workDir = Path.Combine(Path.GetTempPath(), "RDG.Zapret-zapret2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            var zipPath = Path.Combine(workDir, "zapret2.zip");
            var download = await DownloadPackAsync(zipPath, progress, cancellationToken).ConfigureAwait(false);
            if (!download.Success)
                return new Zapret2InstallResult(false, download.Error, 0, null);

            progress?.Report(new DownloadProgress(55, "Распаковываю zapret2…"));
            var extractDir = Path.Combine(workDir, "extract");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            var source = FindPackRoot(extractDir);
            if (source is null)
                return new Zapret2InstallResult(false, "В архиве zapret2 не найден bin\\winws2.exe", 0, null);

            progress?.Report(new DownloadProgress(62, "Устанавливаю zapret2…"));
            CopyPack(source, target);

            // A reinstall must refresh the vendored community files even when
            // the previous pack already carried our marker.
            try { File.Delete(Path.Combine(target, "kinlay0.version")); } catch { /* ignored */ }

            progress?.Report(new DownloadProgress(70, "Готовлю пресеты zapret2…"));
            var rendered = await RenderPresetsAsync(target, progress, cancellationToken).ConfigureAwait(false);

            if (rendered == 0)
                return new Zapret2InstallResult(false, "Не удалось подготовить ни один пресет zapret2", 0, download.Version);

            progress?.Report(new DownloadProgress(96, "Добавляю стратегии сообщества…"));
            var extra = await InstallExtraStrategiesAsync(target, progress, cancellationToken).ConfigureAwait(false);

            var presetTotal = Directory.EnumerateFiles(target, "*.cmd", SearchOption.TopDirectoryOnly).Count() +
                              Directory.EnumerateFiles(target, "kinlay0 (*).bat", SearchOption.TopDirectoryOnly).Count();

            if (!string.IsNullOrWhiteSpace(download.Version))
                File.WriteAllText(Path.Combine(target, "version.txt"), download.Version);

            progress?.Report(new DownloadProgress(100, $"zapret2 готов: {presetTotal} стратегий"));
            _log.Success("zapret2", $"zapret2 установлен в {target}: {presetTotal} стратегий" +
                                    (extra > 0 ? $" (добавлено сообществом: {extra})" : string.Empty));

            return new Zapret2InstallResult(true, null, presetTotal, download.Version);
        }
        catch (OperationCanceledException)
        {
            return new Zapret2InstallResult(false, "Установка отменена", 0, null);
        }
        catch (Exception ex)
        {
            _log.Error("zapret2", "Ошибка установки zapret2: " + ex);
            return new Zapret2InstallResult(false, ex.Message, 0, null);
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

    private async Task<PackDownload> DownloadPackAsync(
        string destination,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (AppEndpoints.IsConfigured)
        {
            var mirror = await TryMirrorAsync(destination, progress, cancellationToken).ConfigureAwait(false);
            if (mirror.Success)
                return mirror;

            _log.Warn("zapret2", "Зеркало zapret2 недоступно, пробую GitHub: " + mirror.Error);
        }

        return await TryGitHubAsync(destination, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PackDownload> TryMirrorAsync(
        string destination,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(TimeSpan.FromMinutes(10));
            progress?.Report(new DownloadProgress(2, "Получаю zapret2 с сервера обновлений…"));

            string? version = null;
            string? sha = null;
            long size = 0;

            try
            {
                var json = await client.GetStringAsync(MirrorManifest, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("version", out var v)) version = v.GetString();
                if (root.TryGetProperty("sha256", out var h)) sha = h.GetString();
                if (root.TryGetProperty("size", out var s) && s.TryGetInt64(out var parsed)) size = parsed;
            }
            catch (Exception ex)
            {
                _log.Debug("zapret2", "Манифест zapret2 недоступен: " + ex.Message);
            }

            await DownloadFileAsync(client, MirrorZip, destination, size, progress, cancellationToken).ConfigureAwait(false);
            if (!await VerifyAsync(destination, sha, cancellationToken).ConfigureAwait(false))
                return new PackDownload(false, "SHA-256 zip zapret2 не совпал", null);

            return new PackDownload(true, null, version);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PackDownload(false, ex.Message, null);
        }
    }

    private async Task<PackDownload> TryGitHubAsync(
        string destination,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(TimeSpan.FromMinutes(10));
            progress?.Report(new DownloadProgress(2, "Ищу zapret2 на GitHub…"));

            string? version = null;
            string? url = null;
            string? sha = null;
            long size = 0;

            try
            {
                using var response = await client.GetAsync(GitHubApi, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;
                    if (root.TryGetProperty("tag_name", out var tag)) version = tag.GetString();

                    if (root.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                            if (name is null || !name.StartsWith("zapret2-next-", StringComparison.OrdinalIgnoreCase) ||
                                !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                continue;

                            url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                            if (asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var parsedSize)) size = parsedSize;
                            if (asset.TryGetProperty("digest", out var d))
                            {
                                var digest = d.GetString();
                                if (!string.IsNullOrWhiteSpace(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                                    sha = digest.Substring("sha256:".Length);
                            }

                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Debug("zapret2", "GitHub API недоступен: " + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                var html = await client.GetStringAsync(GitHubPage, cancellationToken).ConfigureAwait(false);
                var match = GitHubZipRegex.Match(html);
                if (match.Success)
                    url = match.Groups["url"].Value;
            }

            if (string.IsNullOrWhiteSpace(url))
                return new PackDownload(false, "Не удалось найти zapret2-next.zip на GitHub", null);

            await DownloadFileAsync(client, url, destination, size, progress, cancellationToken).ConfigureAwait(false);
            if (!await VerifyAsync(destination, sha, cancellationToken).ConfigureAwait(false))
                return new PackDownload(false, "SHA-256 zip zapret2 не совпал", null);

            return new PackDownload(true, null, version);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PackDownload(false, ex.Message, null);
        }
    }

    private static string? FindPackRoot(string extractDir)
    {
        if (File.Exists(Path.Combine(extractDir, "bin", "winws2.exe")))
            return extractDir;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(extractDir, "*", SearchOption.AllDirectories))
            {
                if (File.Exists(Path.Combine(directory, "bin", "winws2.exe")))
                    return directory;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private void CopyPack(string source, string target)
    {
        Directory.CreateDirectory(target);

        // Preserve user-edited lists when updating an existing pack.
        var userLists = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var targetLists = Path.Combine(target, "lists");
        foreach (var name in new[] { "list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt", "ipset-user.txt" })
        {
            var path = Path.Combine(targetLists, name);
            try
            {
                if (File.Exists(path))
                    userLists[name] = File.ReadAllBytes(path);
            }
            catch
            {
                // ignored
            }
        }

        foreach (var folder in new[] { "bin", "lua", "lists", "presets", "utils" })
        {
            var from = Path.Combine(source, folder);
            if (Directory.Exists(from))
                CopyDirectory(from, Path.Combine(target, folder));
        }

        foreach (var file in new[] { "ENGINE_VERSION", "LICENSE.txt", "THIRD_PARTY_NOTICES.md", "README.md" })
        {
            var from = Path.Combine(source, file);
            if (File.Exists(from))
                File.Copy(from, Path.Combine(target, file), overwrite: true);
        }

        Directory.CreateDirectory(Path.Combine(target, "runtime"));

        foreach (var (name, bytes) in userLists)
        {
            try
            {
                Directory.CreateDirectory(targetLists);
                File.WriteAllBytes(Path.Combine(targetLists, name), bytes);
            }
            catch
            {
                // ignored
            }
        }
    }

    private async Task<int> RenderPresetsAsync(
        string packDir,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var presetsDir = Path.Combine(packDir, "presets");
        var runtimeDir = Path.Combine(packDir, "runtime");
        Directory.CreateDirectory(runtimeDir);

        if (!Directory.Exists(presetsDir))
            return 0;

        var templates = Directory.EnumerateFiles(presetsDir, "*.txt.in", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith("_", StringComparison.Ordinal))
            .OrderBy(path => path, NaturalStringComparer.Instance)
            .ToList();

        var rendered = 0;
        for (var i = 0; i < templates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(templates[i]);
            var presetName = fileName.Substring(0, fileName.Length - ".txt.in".Length);
            var output = Path.Combine(runtimeDir, presetName + ".txt");
            var validation = Path.Combine(runtimeDir, presetName + ".validate.txt");

            var percent = templates.Count == 0 ? 70 : 70 + i * 29.0 / templates.Count;
            progress?.Report(new DownloadProgress(
                Math.Clamp(percent, 70, 99),
                $"Готовлю пресет zapret2: {presetName}"));

            if (!await RenderOneAsync(packDir, presetName, output, validation, cancellationToken).ConfigureAwait(false))
            {
                _log.Warn("zapret2", "Пропущен пресет zapret2: " + presetName);
                continue;
            }

            WriteCmdPreset(packDir, presetName);
            rendered++;
        }

        return rendered;
    }

    private async Task<bool> RenderOneAsync(
        string packDir,
        string presetName,
        string output,
        string validation,
        CancellationToken cancellationToken)
    {
        var script = Path.Combine(packDir, "utils", "render-config.ps1");
        if (!File.Exists(script))
            return false;

        var args =
            "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" " +
            "-Preset \"" + presetName + "\" " +
            "-Output \"" + output + "\" " +
            "-ValidationOutput \"" + validation + "\"";

        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = args,
            WorkingDirectory = packDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return false;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var all = Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(cancellationToken));
            var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken))
                .ConfigureAwait(false);

            if (finished != all)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignored
                }

                _log.Warn("zapret2", $"Тайм-аут подготовки пресета {presetName}");
                return false;
            }

            await all.ConfigureAwait(false);

            if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
            {
                _log.Warn("zapret2",
                    $"Пресет {presetName}: код {process.ExitCode}. {stderrTask.Result.Trim()} {stdoutTask.Result.Trim()}");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("zapret2", $"Не удалось подготовить пресет {presetName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Adds community strategies (kinlay0 .bat + yt4k/discord-light Lua) to an
    /// already installed pack. Safe to call on every start: existing files are
    /// only replaced when the marker is missing/outdated.
    /// </summary>
    public async Task<int> EnsureExtraStrategiesAsync(string root, CancellationToken cancellationToken = default)
    {
        var packDir = FolderPath(root);
        if (!IsInstalled(root))
            return 0;

        return await InstallExtraStrategiesAsync(packDir, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> InstallExtraStrategiesAsync(
        string packDir,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        const string markerVersion = "1.7";
        var marker = Path.Combine(packDir, "kinlay0.version");
        var luaDir = Path.Combine(packDir, "lua");
        var binDir = Path.Combine(packDir, "bin");
        var fakeDir = Path.Combine(binDir, "fake");
        var runtimeDir = Path.Combine(packDir, "runtime");

        try
        {
            var kinlayCount = Directory.Exists(packDir)
                ? Directory.EnumerateFiles(packDir, "kinlay0 (*).bat", SearchOption.TopDirectoryOnly).Count()
                : 0;
            var ytRuntime = Path.Combine(runtimeDir, "YouTube 4K (yt4k).txt");
            var runtimePathsOk = File.Exists(ytRuntime) &&
                                 File.ReadAllText(ytRuntime).Contains(
                                     Slash(Path.Combine(packDir, "bin")), StringComparison.OrdinalIgnoreCase);

            if (File.Exists(marker) &&
                File.ReadAllText(marker).Trim() == markerVersion &&
                kinlayCount >= 26 &&
                runtimePathsOk &&
                File.Exists(Path.Combine(luaDir, "zapret-yt4k.lua")) &&
                File.Exists(Path.Combine(luaDir, "blobs.lua")) &&
                File.Exists(Path.Combine(packDir, "YouTube 4K (yt4k).cmd")) &&
                File.Exists(Path.Combine(packDir, "Discord Light (lua).cmd")))
            {
                return 0;
            }

            Directory.CreateDirectory(luaDir);
            Directory.CreateDirectory(fakeDir);
            Directory.CreateDirectory(runtimeDir);

            // The community .bat files expect these two UDP fake files.
            WriteEmbeddedBlob(fakeDir, "ACTIVE_DISCORD_UDP.bin");
            WriteEmbeddedBlob(fakeDir, "ACTIVE_GAME_UDP.bin");

            WriteEmbedded("blobs.lua", Path.Combine(luaDir, "blobs.lua"));
            WriteEmbedded("zapret-yt4k.lua", Path.Combine(luaDir, "zapret-yt4k.lua"));
            WriteEmbedded("zapret-discord-light.lua", Path.Combine(luaDir, "zapret-discord-light.lua"));
            WriteEmbedded("LICENSE", Path.Combine(packDir, "kinlay0-LICENSE.txt"));

            var added = 0;
            var assembly = typeof(Zapret2PackService).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames())
            {
                if (!resource.Contains(".Resources.kinlay0.", StringComparison.Ordinal) ||
                    !resource.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();

                var prefix = ".Resources.kinlay0.";
                var prefixIndex = resource.IndexOf(prefix, StringComparison.Ordinal);
                var originalName = prefixIndex >= 0
                    ? resource[(prefixIndex + prefix.Length)..]
                    : resource;
                var variant = Regex.Match(originalName, @"^general \((?<v>.+)\)\.bat$", RegexOptions.IgnoreCase);
                var outputName = variant.Success
                    ? $"kinlay0 ({variant.Groups["v"].Value}).bat"
                    : "kinlay0 " + originalName;

                var content = ReadEmbeddedText(resource);
                if (content is null)
                    continue;

                content = content
                    .Replace("..\\lists\\", "%~dp0lists\\", StringComparison.OrdinalIgnoreCase)
                    .Replace("..\\lua\\", "%~dp0lua\\", StringComparison.OrdinalIgnoreCase)
                    .Replace("../lists/", "%~dp0lists/", StringComparison.OrdinalIgnoreCase)
                    .Replace("../lua/", "%~dp0lua/", StringComparison.OrdinalIgnoreCase);

                File.WriteAllText(Path.Combine(packDir, outputName), content, new UTF8Encoding(false));
                added++;
            }

            WriteLuaStrategies(packDir);
            File.WriteAllText(marker, markerVersion, new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(packDir, "kinlay0-README.txt"),
                "Стратегии сообщества kinlay0 (MIT). Источник: kinlay0/zapret2-discord-youtube-strats.\n" +
                "Авторы исходных приёмов: bol-van, Flowseal, kinlay0.\n" +
                "Lua-профили yt4k / discord-light: mqclass/zapret2-gui (MIT).\n",
                new UTF8Encoding(false));

            progress?.Report(new DownloadProgress(99, $"Добавлено стратегий сообщества: {added + 2}"));
            _log.Info("zapret2", $"Добавлены стратегии kinlay0 и Lua-профили: {added + 2}");
            return added + 2;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("zapret2", "Не удалось добавить стратегии сообщества: " + ex.Message);
            return 0;
        }
    }

    private static void WriteEmbeddedBlob(string fakeDir, string name)
    {
        var target = Path.Combine(fakeDir, name);
        if (File.Exists(target))
            return;

        var content = ReadEmbeddedBytes(name);
        if (content is not null && content.Length > 0)
        {
            File.WriteAllBytes(target, content);
            return;
        }

        // Fallback: a small valid-looking UDP payload. Better than failing the
        // whole strategy because a community blob is unavailable.
        File.WriteAllBytes(target, new byte[64]);
    }

    private static byte[]? ReadEmbeddedBytes(string suffix)
    {
        var assembly = typeof(Zapret2PackService).Assembly;
        var names = assembly.GetManifestResourceNames();
        var name = names.FirstOrDefault(n =>
            n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return null;

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
            return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void WriteLuaStrategies(string packDir)
    {
        var bin = Slash(Path.Combine(packDir, "bin"));
        var lua = Slash(Path.Combine(packDir, "lua"));
        var lists = Slash(Path.Combine(packDir, "lists"));
        var fake = Slash(Path.Combine(packDir, "bin", "fake"));
        var filters = Slash(Path.Combine(packDir, "bin", "windivert.filter"));
        var runtime = Path.Combine(packDir, "runtime");

        var common = new StringBuilder();
        common.AppendLine("--debug=0");
        common.AppendLine($"--chdir=\"{bin}\"");
        common.AppendLine($"--lua-init=@\"{lua}/zapret-lib.lua\"");
        common.AppendLine($"--blob=tls_google:@\"{fake}/tls_clienthello_www_google_com.bin\"");
        common.AppendLine($"--blob=quic_google:@\"{fake}/quic_initial_www_google_com.bin\"");
        common.AppendLine($"--blob=discord_udp:@\"{fake}/stun.bin\"");
        common.AppendLine($"--blob=http_iana:@\"{fake}/http_iana_org.bin\"");
        common.AppendLine("--wf-tcp-out=80,443,2053,2083,2087,2096,8443");
        common.AppendLine("--wf-udp-out=443,19294-19344,50000-50100");
        foreach (var filter in new[] { "discord_media", "stun", "wireguard", "quic_initial_ietf" })
        {
            var path = Path.Combine(packDir, "bin", "windivert.filter", "windivert_part." + filter + ".txt");
            if (File.Exists(path))
                common.AppendLine($"--wf-raw-part=@\"{filters}/windivert_part.{filter}.txt\"");
        }

        var listLines = new StringBuilder();
        listLines.AppendLine($"--hostlist=\"{lists}/list-general.txt\"");
        listLines.AppendLine($"--hostlist=\"{lists}/list-general-user.txt\"");
        listLines.AppendLine($"--hostlist-exclude=\"{lists}/list-exclude.txt\"");
        listLines.AppendLine($"--hostlist-exclude=\"{lists}/list-exclude-user.txt\"");
        listLines.AppendLine($"--ipset-exclude=\"{lists}/ipset-exclude.txt\"");
        listLines.AppendLine($"--ipset-exclude=\"{lists}/ipset-exclude-user.txt\"");

        var yt4k = new StringBuilder();
        yt4k.AppendLine("# RDG.Zapret · YouTube 4K (Lua yt4k, сообщество)");
        yt4k.Append(common);
        yt4k.AppendLine($"--lua-init=@\"{lua}/zapret-yt4k.lua\"");
        yt4k.AppendLine();
        yt4k.AppendLine("--filter-tcp=80,443,2053,2083,2087,2096,8443");
        yt4k.Append(listLines);
        yt4k.AppendLine("--payload=tls_client_hello");
        yt4k.AppendLine("--lua-desync=fake:blob=tls_google:repeats=6:tcp_ts=-600000:ip_id=zero");
        yt4k.AppendLine("--lua-desync=multisplit:pos=2,midsld:ip_id=zero");
        yt4k.AppendLine("--payload=http_req");
        yt4k.AppendLine("--lua-desync=fake:blob=http_iana:repeats=6:tcp_ts=-600000");
        yt4k.AppendLine("--lua-desync=multisplit:pos=2");
        yt4k.AppendLine("--new");
        yt4k.AppendLine();
        yt4k.AppendLine("# Discord voice");
        yt4k.AppendLine("--filter-udp=19294-19344,50000-50100");
        yt4k.AppendLine("--filter-l7=discord,stun");
        yt4k.AppendLine("--payload=all");
        yt4k.AppendLine("--out-range=n1-n4");
        yt4k.AppendLine("--lua-desync=fake:blob=discord_udp:repeats=6");
        yt4k.AppendLine("--new");
        yt4k.AppendLine();
        yt4k.AppendLine("# YouTube QUIC");
        yt4k.AppendLine("--filter-udp=443");
        yt4k.AppendLine($"--hostlist=\"{lists}/list-google.txt\"");
        yt4k.AppendLine("--payload=quic_initial");
        yt4k.AppendLine("--lua-desync=fake:blob=quic_google:repeats=11");

        var light = new StringBuilder();
        light.AppendLine("# RDG.Zapret · Discord Light (Lua discord-light, сообщество)");
        light.Append(common);
        light.AppendLine($"--lua-init=@\"{lua}/zapret-discord-light.lua\"");
        light.AppendLine();
        light.AppendLine("--filter-tcp=80,443,2053,2083,2087,2096,8443");
        light.Append(listLines);
        light.AppendLine("--payload=tls_client_hello");
        light.AppendLine("--lua-desync=fake:blob=tls_google");
        light.AppendLine("--lua-desync=multisplit:pos=2");
        light.AppendLine("--payload=http_req");
        light.AppendLine("--lua-desync=fake:blob=http_iana");
        light.AppendLine("--lua-desync=multisplit:pos=2");
        light.AppendLine("--new");
        light.AppendLine();
        light.AppendLine("# Discord voice");
        light.AppendLine("--filter-udp=19294-19344,50000-50100");
        light.AppendLine("--filter-l7=discord,stun");
        light.AppendLine("--payload=all");
        light.AppendLine("--out-range=n1-n4");
        light.AppendLine("--lua-desync=fake:blob=discord_udp:repeats=6");
        light.AppendLine("--new");
        light.AppendLine();
        light.AppendLine("# YouTube QUIC");
        light.AppendLine("--filter-udp=443");
        light.AppendLine($"--hostlist=\"{lists}/list-google.txt\"");
        light.AppendLine("--payload=quic_initial");
        light.AppendLine("--lua-desync=fake:blob=quic_google:repeats=6");

        WriteRuntime(packDir, runtime, "YouTube 4K (yt4k)", yt4k.ToString());
        WriteRuntime(packDir, runtime, "Discord Light (lua)", light.ToString());
    }

    private static void WriteRuntime(string packDir, string runtimeDir, string name, string content)
    {
        var runtimePath = Path.Combine(runtimeDir, name + ".txt");
        File.WriteAllText(runtimePath, content, new UTF8Encoding(false));

        var cmd =
            "@echo off\r\n" +
            "start \"\" /b \"%~dp0bin\\winws2.exe\" @\"%~dp0runtime\\" + name + ".txt\"\r\n";
        File.WriteAllText(Path.Combine(packDir, name + ".cmd"), cmd, new UTF8Encoding(false));
    }

    private static string Slash(string path) => path.Replace('\\', '/');

    private static void WriteEmbedded(string suffix, string destination)
    {
        var content = ReadEmbeddedText(suffix);
        if (content is not null)
            File.WriteAllText(destination, content, new UTF8Encoding(false));
    }

    private static string? ReadEmbeddedText(string suffix)
    {
        var assembly = typeof(Zapret2PackService).Assembly;
        var names = assembly.GetManifestResourceNames();
        var name = names.FirstOrDefault(n =>
            n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return null;

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void WriteCmdPreset(string packDir, string presetName)
    {
        var cmd =
            "@echo off\r\n" +
            "start \"\" /b \"%~dp0bin\\winws2.exe\" @\"%~dp0runtime\\" + presetName + ".txt\"\r\n";
        File.WriteAllText(Path.Combine(packDir, presetName + ".cmd"), cmd, new UTF8Encoding(false));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
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

                var percent = total > 0 ? 2 + read * 50.0 / total : 20;
                var status = total > 0
                    ? $"Скачиваю zapret2… {FormatBytes(read)} из {FormatBytes(total)}"
                    : $"Скачиваю zapret2… {FormatBytes(read)}";
                progress?.Report(new DownloadProgress(Math.Clamp(percent, 2, 52), status));
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> VerifyAsync(string path, string? expected, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            // The mirror manifest and GitHub API both provide a hash. If neither
            // is available, still accept the file but log it.
            return true;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
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
            return $"{bytes / 1024.0 / 1024.0:0.0} МБ";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:0} КБ";
        return bytes + " Б";
    }

    private sealed record PackDownload(bool Success, string? Error, string? Version);
}
