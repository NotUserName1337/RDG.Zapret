using System.Text.Json;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly LogService _log;

    public SettingsService(LogService log)
    {
        _log = log;
        DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RDG.Zapret");
        FilePath = Path.Combine(DirectoryPath, "settings.json");
    }

    public string DirectoryPath { get; }
    public string FilePath { get; }
    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public AppSettings Load()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    Current = Normalize(loaded);
                    _log.Debug("settings", $"Настройки загружены: {FilePath}");
                    return Current;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("settings", $"Не удалось прочитать настройки: {ex.Message}");
            TryBackupBrokenFile();
        }

        Current = new AppSettings();
        return Current;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn("settings", $"Не удалось сохранить настройки: {ex.Message}");
        }
    }

    public void Update(Action<AppSettings> mutate, bool notify = true)
    {
        mutate(Current);
        Current = Normalize(Current);
        Save();
        if (notify)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        Current = new AppSettings();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.ZapretPath ??= string.Empty;
        settings.LastPresetFile ??= string.Empty;
        settings.BestPresetFile ??= string.Empty;
        settings.BestScoreDate ??= string.Empty;
        settings.LastTuneDate ??= string.Empty;
        settings.LastUpdateCheckDate ??= string.Empty;
        settings.StrategyPackUrl ??= string.Empty;
        settings.StrategyPackHash ??= string.Empty;
        settings.StrategyPackVersion ??= string.Empty;
        settings.LastStrategyPackCheckDate ??= string.Empty;
        settings.TgProxySecret ??= string.Empty;
        settings.Accent ??= "violet";
        settings.Engine ??= "zapret";
        settings.TuneMode ??= "standard";
        settings.GeneratorMode ??= "full";
        settings.TuneDisabledServices ??= new List<string>();
        settings.TuneExcludedPresets ??= new List<string>();
        settings.CustomServices ??= new List<CustomService>();
        settings.YoutubeHostsFixAppliedAt ??= string.Empty;

        settings.ProbeTimeoutSeconds = Math.Clamp(settings.ProbeTimeoutSeconds, 2, 15);
        settings.TuneWarmupSeconds = Math.Clamp(settings.TuneWarmupSeconds, 2, 12);
        settings.TuneFinalistLimit = Math.Clamp(settings.TuneFinalistLimit, 3, 16);
        settings.BackgroundCheckIntervalMinutes = Math.Clamp(settings.BackgroundCheckIntervalMinutes, 1, 120);
        settings.BackgroundRetryAttempts = Math.Clamp(settings.BackgroundRetryAttempts, 1, 10);
        settings.BackgroundRetryDelaySeconds = Math.Clamp(settings.BackgroundRetryDelaySeconds, 2, 60);
        settings.TuneBestLimit = Math.Clamp(settings.TuneBestLimit, 3, 30);
        settings.TgProxyPort = Math.Clamp(settings.TgProxyPort, 1024, 65535);
        if (settings.TuneMode is not ("quick" or "standard" or "deep"))
            settings.TuneMode = "standard";
        if (settings.GeneratorMode is not ("fast" or "full" or "max"))
            settings.GeneratorMode = "full";
        if (settings.Accent is not ("violet" or "ocean" or "emerald" or "sunset"))
            settings.Accent = "violet";
        if (settings.Engine is not ("zapret" or "zapret2"))
            settings.Engine = "zapret";

        return settings;
    }

    private void TryBackupBrokenFile()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;
            var backup = FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(FilePath, backup, overwrite: true);
            _log.Warn("settings", $"Повреждённый файл настроек сохранён как {Path.GetFileName(backup)}");
        }
        catch
        {
            // ignored
        }
    }
}
