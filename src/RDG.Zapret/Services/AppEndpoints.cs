using System.Text.Json;

namespace RDG.Zapret.Services;

/// <summary>
/// Central place for every remote endpoint used by update checks, strategy
/// packs, mirror downloads and the dashboard banner.
///
/// The repository ships a neutral placeholder. Point the app at your own
/// hosting in one of two ways (checked in this order):
///   1. environment variable RDG_UPDATE_BASE_URL;
///   2. update-endpoints.json next to RDG.Zapret.exe:
///        { "baseUrl": "https://your-domain.example/zapret" }
///
/// The configured base URL must serve version.json, releases.json,
/// strategies.zip, banner.json and the optional mirror files.
/// </summary>
public static class AppEndpoints
{
    public const string PlaceholderBaseUrl = "https://updates.example.com/zapret";

    private static readonly Lazy<string> LazyBaseUrl = new(ResolveBaseUrl);

    public static string BaseUrl => LazyBaseUrl.Value;

    /// <summary>True when a real base URL is configured instead of the placeholder.</summary>
    public static bool IsConfigured =>
        !string.Equals(
            BaseUrl.TrimEnd('/'),
            PlaceholderBaseUrl.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Scheme and host of the configured base URL.</summary>
    public static string Origin =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : PlaceholderBaseUrl;

    public static string SiteUrl => BaseUrl + "/";

    // App updates and the strategy bundle.
    public static string ManifestUrl => Url("version.json");
    public static string ReleasesUrl => Url("releases.json");
    public static string DefaultPackUrl => Url("strategies.zip");

    // Dashboard banner.
    public static string BannerUrl => Url("banner.json");

    // Optional mirror for the zapret2 pack.
    public static string Zapret2ManifestUrl => Url("zapret2.json");
    public static string Zapret2PackUrl => Url("zapret2-pack.zip");

    // Optional mirror for TgWsProxy.
    public static string TgProxyManifestUrl => Url("tgwsproxy.json");
    public static string TgProxyExeUrl => Url("TgWsProxy.exe");

    /// <summary>Combines the configured base URL with a relative path.</summary>
    public static string Url(string relative)
    {
        var value = (relative ?? string.Empty).Trim();
        if (value.Length == 0)
            return BaseUrl;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return value;
        if (value.StartsWith("/", StringComparison.Ordinal))
            return Origin + value;
        return BaseUrl + "/" + value.TrimStart('/');
    }

    /// <summary>Resolves the bundle value from releases.json.</summary>
    public static string ResolveBundle(string bundle)
    {
        var value = (bundle ?? string.Empty).Trim();
        if (value.Length == 0)
            return DefaultPackUrl;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return value;
        if (value.StartsWith("/", StringComparison.Ordinal))
            return Origin + value;
        if (value.StartsWith("releases/", StringComparison.OrdinalIgnoreCase))
            return BaseUrl + "/" + value.TrimStart('/');
        return BaseUrl + "/releases/" + value.TrimStart('/');
    }

    private static string ResolveBaseUrl()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("RDG_UPDATE_BASE_URL");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return Normalize(fromEnvironment);

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "update-endpoints.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("baseUrl", out var element))
                {
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return Normalize(value);
                }
            }
        }
        catch
        {
            // A broken config file must never prevent the app from starting.
        }

        return PlaceholderBaseUrl;
    }

    private static string Normalize(string value) => value.Trim().TrimEnd('/');
}
