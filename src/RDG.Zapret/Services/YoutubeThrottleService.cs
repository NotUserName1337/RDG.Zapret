using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

/// <summary>
/// Detects the "YouTube opens, but video crawls at ~17 KB/s" case: a specific
/// googlevideo CDN node is throttled while the site itself is reachable.
/// The check downloads a ~3 MiB range from a few real videoplayback nodes.
/// </summary>
public sealed class YoutubeThrottleService
{
    private const long RangeBytes = 3 * 1024 * 1024;
    private const double SlowThresholdMbps = 1.5;

    private static readonly string[] TestVideoIds = { "aqz-KE-bpKQ", "dQw4w9WgXcQ", "LXb3EKWsInQ" };

    private static readonly Regex UrlRegex = new(
        "\"(?<url>https?:(?:\\\\?/){2}[^\"]*videoplayback[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NodeHostRegex = new(
        "^rr\\d+---sn-[a-z0-9-]+\\.googlevideo\\.com$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<YoutubeThrottleReport> CheckAsync(CancellationToken cancellationToken)
    {
        var urls = new List<string>();
        using var client = CreateClient();

        foreach (var id in TestVideoIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                urls.AddRange(await GetPlayerUrlsAsync(client, id, cancellationToken).ConfigureAwait(false));
                if (urls.Select(u => TryHost(u)).Where(IsValidNodeHost).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 3)
                    break;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // player API timeout: try the next public test video
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // try the next public test video
            }
        }

        // Fallback for API changes: the watch page sometimes still exposes
        // a direct videoplayback URL.
        if (urls.Count == 0)
        {
            foreach (var id in TestVideoIds)
            {
                try
                {
                    using var pageCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    pageCts.CancelAfter(TimeSpan.FromSeconds(12));
                    var html = await client.GetStringAsync(
                        $"https://www.youtube.com/watch?v={id}&hl=en&bpctr=9999999999",
                        pageCts.Token).ConfigureAwait(false);
                    urls.AddRange(ExtractPlaybackUrls(html));
                    if (urls.Count > 0)
                        break;
                }
                catch
                {
                    // try the next public test video
                }
            }
        }

        return await BuildReportAsync(client, urls, cancellationToken).ConfigureAwait(false);
    }

    public async Task<YoutubeThrottleReport> CheckVideoAsync(
        string input,
        CancellationToken cancellationToken)
    {
        var videoId = ExtractVideoId(input);
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return new YoutubeThrottleReport
            {
                Verdict = YoutubeVerdict.Unreachable,
                Title = "Не удалось найти видео",
                Message = "Вставьте ссылку на YouTube или ID ролика (11 символов)."
            };
        }

        using var client = CreateClient();
        var urls = new List<string>();
        try
        {
            urls.AddRange(await GetPlayerUrlsAsync(client, videoId, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // fall back to the watch page below
        }

        if (urls.Count == 0)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(12));
                var html = await client.GetStringAsync(
                    $"https://www.youtube.com/watch?v={videoId}&hl=en&bpctr=9999999999",
                    cts.Token).ConfigureAwait(false);
                urls.AddRange(ExtractPlaybackUrls(html));
            }
            catch
            {
                // report below
            }
        }

        var report = await BuildReportAsync(client, urls, cancellationToken, perVideo: true).ConfigureAwait(false);
        return report;
    }

    public static string? ExtractVideoId(string input)
    {
        var value = (input ?? string.Empty).Trim();
        if (value.Length == 0)
            return null;

        if (Regex.IsMatch(value, "^[A-Za-z0-9_-]{11}$"))
            return value;

        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;

        var query = uri.Query;
        var match = Regex.Match(query, "[?&]v=([A-Za-z0-9_-]{11})");
        if (match.Success)
            return match.Groups[1].Value;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 &&
            (segments[0].Equals("shorts", StringComparison.OrdinalIgnoreCase) ||
             segments[0].Equals("embed", StringComparison.OrdinalIgnoreCase) ||
             segments[0].Equals("live", StringComparison.OrdinalIgnoreCase)))
        {
            if (Regex.IsMatch(segments[1], "^[A-Za-z0-9_-]{11}$"))
                return segments[1];
        }

        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) &&
            segments.Length >= 1 &&
            Regex.IsMatch(segments[0], "^[A-Za-z0-9_-]{11}$"))
            return segments[0];

        return null;
    }

    private static async Task<YoutubeThrottleReport> BuildReportAsync(
        HttpClient client,
        IEnumerable<string> urls,
        CancellationToken cancellationToken,
        bool perVideo = false)
    {
        var nodes = urls
            .Select(url => (Url: url, Host: TryHost(url)))
            .Where(x => x.Host is not null && IsValidNodeHost(x.Host))
            .GroupBy(x => x.Host!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(3)
            .ToList();

        if (nodes.Count == 0)
        {
            return new YoutubeThrottleReport
            {
                Verdict = YoutubeVerdict.Unreachable,
                Title = "Не удалось проверить видео",
                Message = perVideo
                    ? "YouTube не отдал ссылки на видеопоток. Возможно, видео недоступно, закрыто по возрасту или обход не работает. " +
                      "Если через VPN это видео открывается — нажмите «YouTube QUIC fix» и отключите QUIC/HTTP3 в браузере."
                    : "YouTube не отдал ссылки на видеопоток. Возможно, видео недоступно, закрыто по возрасту или обход не работает."
            };
        }

        var results = new List<YoutubeNodeResult>(nodes.Count);
        foreach (var (url, host) in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await MeasureNodeAsync(client, url, host!, cancellationToken).ConfigureAwait(false));
        }

        var slow = results.Where(r => r.IsThrottled && r.Mbps > 0).ToList();
        var unreachable = results.Where(r => r.Mbps <= 0).ToList();

        var verdict = slow.Count > 0
            ? YoutubeVerdict.Slow
            : unreachable.Count == results.Count ? YoutubeVerdict.Unreachable : YoutubeVerdict.Fast;

        var title = verdict switch
        {
            YoutubeVerdict.Fast => perVideo ? "Видео загружается быстро" : "YouTube работает быстро",
            YoutubeVerdict.Slow => perVideo ? "Видео отдаётся медленно" : "Найдено замедление YouTube",
            _ => "Серверы видео недоступны"
        };

        var message = verdict switch
        {
            YoutubeVerdict.Fast =>
                $"Скорость видео в норме: {string.Join(", ", results.Select(r => r.SpeedText))}.",
            YoutubeVerdict.Slow when slow.Count == results.Count =>
                $"Все проверенные серверы отдают видео медленно ({string.Join(", ", slow.Select(r => r.SpeedText))}). " +
                "Можно включить исправление — проблемные серверы будут пропускаться.",
            YoutubeVerdict.Slow =>
                $"{slow.Count} из {results.Count} серверов отдают видео медленно. " +
                "Исправление перенаправит только проблемные серверы.",
            _ when perVideo =>
                "Серверы этого видео не отдают данные. Если ролик не грузится ни с одной стратегией — " +
                "нажмите «YouTube QUIC fix» и отключите QUIC/HTTP3 в браузере.",
            _ =>
                "Серверы видео не отдают данные. Проверьте, что обход включён, и повторите проверку. " +
                "Если YouTube открывается, но видео не грузится — запустите автоподбор."
        };

        return new YoutubeThrottleReport
        {
            Verdict = verdict,
            Title = title,
            Message = message,
            Nodes = results,
            SlowHosts = slow.Select(r => r.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            FixableHosts = results
                .Where(r => r.IsThrottled || r.Mbps <= 0)
                .Select(r => r.Host)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static async Task<IReadOnlyList<string>> GetPlayerUrlsAsync(
        HttpClient client,
        string videoId,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var body = JsonSerializer.Serialize(new
        {
            context = new
            {
                client = new
                {
                    clientName = "ANDROID_VR",
                    clientVersion = "1.60.19",
                    hl = "en",
                    gl = "US"
                }
            },
            videoId,
            contentCheckOk = true,
            racyCheckOk = true
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.youtube.com/youtubei/v1/player?key=AIzaSyA8eiZmM1FaDVjRy-df2KTyQ_vz_yYM39w");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "com.google.android.apps.youtube.vr.oculus/1.60.19 (Linux; U; Android 12; en_US; Quest 3 Build/SQ3A.220605.009.A1) gzip");
        request.Headers.TryAddWithoutValidation("X-Youtube-Client-Name", "28");
        request.Headers.TryAddWithoutValidation("X-Youtube-Client-Version", "1.60.19");
        request.Headers.TryAddWithoutValidation("X-Goog-Api-Format-Version", "2");

        using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return Array.Empty<string>();

        var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("streamingData", out var streaming) ||
            streaming.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();

        var urls = new List<string>();
        foreach (var section in new[] { "adaptiveFormats", "formats" })
        {
            if (!streaming.TryGetProperty(section, out var formats) ||
                formats.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var format in formats.EnumerateArray())
            {
                if (!format.TryGetProperty("url", out var urlElement) ||
                    urlElement.ValueKind != JsonValueKind.String)
                    continue;

                var url = urlElement.GetString();
                if (!string.IsNullOrWhiteSpace(url) &&
                    url.Contains("videoplayback", StringComparison.OrdinalIgnoreCase))
                    urls.Add(url);
            }
        }

        return urls;
    }

    private static async Task<YoutubeNodeResult> MeasureNodeAsync(
        HttpClient client,
        string url,
        string host,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        long total = 0;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(7));

            var rangedUrl = Regex.Replace(url, @"([?&])range=[^&]*", "$1", RegexOptions.IgnoreCase);
            rangedUrl = rangedUrl.TrimEnd('?', '&');
            rangedUrl += (rangedUrl.Contains('?') ? "&" : "?") + $"range=0-{RangeBytes - 1}";

            using var request = new HttpRequestMessage(HttpMethod.Get, rangedUrl);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, RangeBytes - 1);
            request.Headers.TryAddWithoutValidation("User-Agent",
                "com.google.android.apps.youtube.vr.oculus/1.60.19 (Linux; U; Android 12; en_US; Quest 3 Build/SQ3A.220605.009.A1) gzip");

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                watch.Stop();
                return new YoutubeNodeResult
                {
                    Host = host,
                    ElapsedMs = (int)watch.ElapsedMilliseconds,
                    IsThrottled = false,
                    Detail = $"HTTP {(int)response.StatusCode} · сервер не отдаёт видео"
                };
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buffer = new byte[65536];
            int read;
            while (total < RangeBytes && (read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (watch.Elapsed > TimeSpan.FromSeconds(5))
                    break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Read as much as we could before the 7s cap.
        }
        catch (Exception ex)
        {
            watch.Stop();
            return new YoutubeNodeResult
            {
                Host = host,
                ElapsedMs = (int)watch.ElapsedMilliseconds,
                IsThrottled = false,
                Detail = "Сервер не ответил: " + Shorten(ex.Message)
            };
        }

        watch.Stop();
        var seconds = Math.Max(0.25, watch.Elapsed.TotalSeconds);
        var mbps = total * 8.0 / seconds / 1_000_000.0;

        if (total == 0)
        {
            return new YoutubeNodeResult
            {
                Host = host,
                ElapsedMs = (int)watch.ElapsedMilliseconds,
                IsThrottled = false,
                Detail = "Сервер не отдал данные"
            };
        }

        return new YoutubeNodeResult
        {
            Host = host,
            Mbps = mbps,
            Bytes = total,
            ElapsedMs = (int)watch.ElapsedMilliseconds,
            IsThrottled = mbps < SlowThresholdMbps,
            Detail = mbps < SlowThresholdMbps ? "Медленно" : "Норма"
        };
    }

    private static IEnumerable<string> ExtractPlaybackUrls(string html)
    {
        foreach (Match match in UrlRegex.Matches(html))
        {
            var value = match.Groups["url"].Value
                .Replace("\\/", "/", StringComparison.Ordinal)
                .Replace("\\u0026", "&", StringComparison.Ordinal)
                .Replace("\\u003d", "=", StringComparison.Ordinal)
                .Replace("&amp;", "&", StringComparison.Ordinal);

            if (value.StartsWith("//", StringComparison.Ordinal))
                value = "https:" + value;

            if (value.Contains("videoplayback%3F", StringComparison.OrdinalIgnoreCase))
            {
                try { value = Uri.UnescapeDataString(value); }
                catch { continue; }
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                yield return uri.ToString();
        }
    }

    private static string? TryHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    public static bool IsValidNodeHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && NodeHostRegex.IsMatch(host);

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 RDG.Zapret/1.0");
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/html"));
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en-US;q=0.8");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Cookie", "CONSENT=YES+cb.20210328-17-p0.en+FX+123");
        return client;
    }

    private static string Shorten(string value)
    {
        value = value.Replace(Environment.NewLine, " ").Trim();
        return value.Length > 100 ? value[..100] + "…" : value;
    }
}

/// <summary>
/// Opt-in hosts-file fix for throttled YouTube nodes. Only the marked section is
/// ever changed; the original hosts file is backed up first.
/// </summary>
public sealed class HostsFixService
{
    public const string MarkerStart = "# >>> RDG.Zapret slow YouTube nodes >>>";
    public const string MarkerEnd = "# <<< RDG.Zapret slow YouTube nodes <<<";
    private const string FixAddress = "127.0.0.2";

    private readonly LogService _log;

    public HostsFixService(LogService log) => _log = log;

    public static string HostsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "drivers", "etc", "hosts");

    public HostsFixStatus GetStatus()
    {
        try
        {
            var lines = File.ReadAllLines(HostsPath);
            var nodes = new List<string>();
            var inside = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Equals(MarkerStart, StringComparison.Ordinal))
                {
                    inside = true;
                    continue;
                }

                if (trimmed.Equals(MarkerEnd, StringComparison.Ordinal))
                    break;

                if (!inside || trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                    continue;

                var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && YoutubeThrottleService.IsValidNodeHost(parts[1]))
                    nodes.Add(parts[1]);
            }

            return new HostsFixStatus
            {
                Applied = nodes.Count > 0,
                NodeCount = nodes.Count,
                HostsPath = HostsPath,
                Nodes = nodes
            };
        }
        catch (Exception ex)
        {
            _log.Debug("hosts", "Не удалось прочитать hosts: " + ex.Message);
            return new HostsFixStatus { HostsPath = HostsPath };
        }
    }

    public async Task<HostsFixResult> ApplyAsync(IEnumerable<string> hosts, CancellationToken cancellationToken = default)
    {
        var valid = hosts
            .Select(h => h.Trim())
            .Where(YoutubeThrottleService.IsValidNodeHost)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (valid.Count == 0)
            return new HostsFixResult(false, "Нет корректных адресов серверов YouTube для исправления.");

        try
        {
            var lines = File.Exists(HostsPath) ? File.ReadAllLines(HostsPath).ToList() : new List<string>();
            Backup(lines);

            lines = RemoveSection(lines);
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add(string.Empty);

            lines.Add(MarkerStart);
            lines.Add("# Автоматически добавлено RDG.Zapret. Удалить можно в приложении.");
            lines.AddRange(valid.Select(h => $"{FixAddress} {h}"));
            lines.Add(MarkerEnd);

            File.WriteAllLines(HostsPath, lines, new UTF8Encoding(false));
            await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", 8000, cancellationToken).ConfigureAwait(false);
            _log.Info("hosts", $"Исправление медленного YouTube включено: {valid.Count} серверов");
            return new HostsFixResult(true, $"Исправление включено: {valid.Count} медленных серверов YouTube.");
        }
        catch (UnauthorizedAccessException)
        {
            return new HostsFixResult(false, "Нужны права администратора, чтобы изменить файл hosts.");
        }
        catch (Exception ex)
        {
            _log.Warn("hosts", "Не удалось изменить hosts: " + ex.Message);
            return new HostsFixResult(false, "Не удалось изменить hosts: " + ex.Message);
        }
    }

    public async Task<HostsFixResult> RemoveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(HostsPath))
                return new HostsFixResult(true, "Файл hosts не найден — исправление уже отключено.");

            var lines = File.ReadAllLines(HostsPath).ToList();
            Backup(lines);
            var cleaned = RemoveSection(lines);
            File.WriteAllLines(HostsPath, cleaned, new UTF8Encoding(false));
            await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", 8000, cancellationToken).ConfigureAwait(false);
            _log.Info("hosts", "Исправление медленного YouTube отключено");
            return new HostsFixResult(true, "Исправление отключено. Файл hosts восстановлен.");
        }
        catch (UnauthorizedAccessException)
        {
            return new HostsFixResult(false, "Нужны права администратора, чтобы изменить файл hosts.");
        }
        catch (Exception ex)
        {
            _log.Warn("hosts", "Не удалось изменить hosts: " + ex.Message);
            return new HostsFixResult(false, "Не удалось изменить hosts: " + ex.Message);
        }
    }

    private static List<string> RemoveSection(List<string> lines)
    {
        var result = new List<string>(lines.Count);
        var skipping = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals(MarkerStart, StringComparison.Ordinal))
            {
                skipping = true;
                continue;
            }

            if (trimmed.Equals(MarkerEnd, StringComparison.Ordinal))
            {
                skipping = false;
                continue;
            }

            if (!skipping)
                result.Add(line);
        }

        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
            result.RemoveAt(result.Count - 1);

        return result;
    }

    private void Backup(List<string> lines)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RDG.Zapret");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "hosts-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            File.WriteAllLines(path, lines, new UTF8Encoding(false));

            foreach (var old in Directory.EnumerateFiles(dir, "hosts-backup-*.txt")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(5))
            {
                try { File.Delete(old); } catch { /* ignored */ }
            }
        }
        catch
        {
            // A backup is best-effort; the marked-section edit itself is safe.
        }
    }
}
