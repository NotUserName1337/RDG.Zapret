using System.Text.Json;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

public sealed class ResultsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly LogService _log;
    private Dictionary<string, StrategyScore> _scores = new(StringComparer.OrdinalIgnoreCase);

    public ResultsService(SettingsService settings, LogService log)
    {
        _log = log;
        FilePath = Path.Combine(settings.DirectoryPath, "results.json");
        Load();
    }

    public string FilePath { get; }

    public event EventHandler? Changed;

    public StrategyScore? Get(string? presetFile)
    {
        if (string.IsNullOrWhiteSpace(presetFile))
            return null;
        return _scores.TryGetValue(presetFile, out var score) ? score : null;
    }

    public IReadOnlyDictionary<string, StrategyScore> All => _scores;

    public void Record(TuneResult result, bool manual)
    {
        var services = result.Services
            .GroupBy(s => s.ServiceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Status.ToString(), StringComparer.OrdinalIgnoreCase);

        _scores[result.Preset.FileName] = new StrategyScore
        {
            PresetFile = result.Preset.FileName,
            Score = result.Score,
            TestedAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm"),
            Services = services,
            IsManual = manual
        };

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public int RemoveBelow(double score)
    {
        var removed = _scores
            .Where(pair => pair.Value.Score < score)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in removed)
            _scores.Remove(key);

        if (removed.Count > 0)
        {
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed.Count;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, StrategyScore>>(json, JsonOptions);
            if (loaded is not null)
            {
                _scores = new Dictionary<string, StrategyScore>(loaded, StringComparer.OrdinalIgnoreCase);
                _log.Debug("results", $"Загружены результаты проверок: {_scores.Count}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("results", "Не удалось прочитать результаты проверок: " + ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_scores, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn("results", "Не удалось сохранить результаты проверок: " + ex.Message);
        }
    }
}
