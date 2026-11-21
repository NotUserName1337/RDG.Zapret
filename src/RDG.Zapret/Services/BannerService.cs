using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

/// <summary>
/// Loads the small dashboard banner from /zapret/banner.json, caches it locally
/// and downloads the banner image. The banner is edited in admin.php → Zapret.
/// </summary>
public sealed class BannerService
{
    public static string ManifestUrl => AppEndpoints.BannerUrl;
    public const string DefaultImage = "";

    private readonly LogService _log;
    private readonly HttpClient _client;
    private readonly string _cacheJson;
    private readonly string _cacheSource;
    private readonly string _cacheImagePrefix;

    public BannerService(LogService log)
    {
        _log = log;
        CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RDG.Zapret",
            "banner");
        _cacheJson = Path.Combine(CacheDirectory, "banner.json");
        _cacheSource = Path.Combine(CacheDirectory, "image-source.txt");
        _cacheImagePrefix = Path.Combine(CacheDirectory, "banner-image");

        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("RDG.Zapret/" + UpdateService.CurrentVersionText);
        _client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
    }

    public string CacheDirectory { get; }

    public async Task<BannerInfo?> LoadAsync(CancellationToken cancellationToken)
    {
        // A build without a configured host must not resurrect an old cached
        // banner from a previous installation.
        if (!AppEndpoints.IsConfigured)
            return null;

        BannerInfo? fetched = null;
        try
        {
            fetched = await FetchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug("banner", "Не удалось загрузить баннер: " + ex.Message);
        }

        var info = fetched ?? LoadCached();
        if (info is null)
            return null;

        info.LocalImagePath = await EnsureImageAsync(info, cancellationToken).ConfigureAwait(false);

        if (fetched is not null)
            SaveCached(fetched);

        return info;
    }

    private async Task<BannerInfo?> FetchAsync(CancellationToken cancellationToken)
    {
        var url = ManifestUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }

    private static BannerInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        var root = document.RootElement;
        var info = new BannerInfo
        {
            Enabled = root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
            Title = GetString(root, "title"),
            Text = GetString(root, "text"),
            ImageUrl = GetString(root, "image"),
            Updated = GetString(root, "updated")
        };

        if (info.ImageUrl.Length > 0)
        {
            if (Uri.TryCreate(info.ImageUrl, UriKind.Relative, out _) &&
                Uri.TryCreate(new Uri(ManifestUrl), info.ImageUrl, out var absolute))
                info.ImageUrl = absolute.ToString();
        }

        if (root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in links.EnumerateArray())
            {
                if (info.Links.Count >= 2)
                    break;

                var url = GetString(link, "url");
                if (!IsSafeUrl(url))
                    continue;

                var label = GetString(link, "label").Trim();
                if (label.Length == 0)
                    label = info.Links.Count == 0 ? "Открыть" : "Ссылка";

                info.Links.Add(new BannerLink(label, url));
            }
        }

        return info;
    }

    private async Task<string> EnsureImageAsync(BannerInfo info, CancellationToken cancellationToken)
    {
        var cachedImage = FindCachedImage();
        if (string.IsNullOrWhiteSpace(info.ImageUrl))
            return cachedImage ?? DefaultImage;

        var extension = GetExtension(info.ImageUrl);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(info.ImageUrl)))
            .ToLowerInvariant()[..10];
        var target = _cacheImagePrefix + "-" + hash + extension;

        try
        {
            if (File.Exists(target) && File.Exists(_cacheSource) &&
                string.Equals(File.ReadAllText(_cacheSource).Trim(), info.ImageUrl, StringComparison.Ordinal))
                return target;
        }
        catch
        {
            // ignored
        }

        try
        {
            Directory.CreateDirectory(CacheDirectory);
            using var response = await _client.GetAsync(info.ImageUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var temp = target + ".tmp";
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = File.Create(temp))
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

            if (new FileInfo(temp).Length < 100)
            {
                File.Delete(temp);
                return cachedImage ?? DefaultImage;
            }

            File.Move(temp, target, overwrite: true);
            File.WriteAllText(_cacheSource, info.ImageUrl);

            try
            {
                foreach (var old in Directory.EnumerateFiles(CacheDirectory, "banner-image-*"))
                {
                    if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
                        File.Delete(old);
                }
            }
            catch
            {
                // ignored
            }

            return target;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug("banner", "Не удалось скачать картинку баннера: " + ex.Message);
            return cachedImage ?? DefaultImage;
        }
    }

    private BannerInfo? LoadCached()
    {
        try
        {
            if (!File.Exists(_cacheJson))
                return null;

            var info = JsonSerializer.Deserialize<BannerInfo>(File.ReadAllText(_cacheJson));
            if (info is null)
                return null;

            info.Links ??= new List<BannerLink>();
            info.LocalImagePath = FindCachedImage() ?? DefaultImage;
            return info;
        }
        catch (Exception ex)
        {
            _log.Debug("banner", "Не удалось прочитать кэш баннера: " + ex.Message);
            return null;
        }
    }

    private void SaveCached(BannerInfo info)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cacheJson, json);
        }
        catch (Exception ex)
        {
            _log.Debug("banner", "Не удалось сохранить кэш баннера: " + ex.Message);
        }
    }

    private string? FindCachedImage()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory))
                return null;

            return Directory.EnumerateFiles(CacheDirectory, "banner-image.*")
                .FirstOrDefault(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static string GetExtension(string url)
    {
        try
        {
            var extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            if (extension is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif")
                return extension;
        }
        catch
        {
            // ignored
        }

        return ".jpg";
    }

    private static string GetString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool IsSafeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
