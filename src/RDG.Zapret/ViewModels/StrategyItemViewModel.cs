using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;

namespace RDG.Zapret.ViewModels;

public sealed class StrategyItemViewModel : ObservableObject
{
    private bool _isBest;
    private bool _isActive;
    private bool _isTesting;
    private double _score;
    private bool _hasScore;
    private string _testedAt = string.Empty;

    public StrategyItemViewModel(StrategyPreset preset)
    {
        Preset = preset;
    }

    public StrategyPreset Preset { get; }
    public string FileName => Preset.FileName;
    public string Name => Preset.DisplayName;
    public string Tag => Preset.Tag;
    public string EngineName => Preset.EngineName;
    public string Arguments => Preset.Arguments;
    public string FilePath => Preset.FilePath;
    public bool IsExperimental => Preset.IsExperimental;

    public double Score
    {
        get => _score;
        private set => SetProperty(ref _score, value);
    }

    public bool HasScore
    {
        get => _hasScore;
        private set
        {
            if (SetProperty(ref _hasScore, value))
                OnPropertyChanged(nameof(ScoreText), nameof(ScoreCaption));
        }
    }

    public string ScoreText => HasScore ? $"{Score:0}" : "—";
    public string ScoreCaption => HasScore ? $"из 100 · {_testedAt}" : "ещё не проверялась";

    public string TestedAt
    {
        get => _testedAt;
        private set
        {
            if (SetProperty(ref _testedAt, value))
                OnPropertyChanged(nameof(ScoreCaption));
        }
    }

    public bool IsBest
    {
        get => _isBest;
        set => SetProperty(ref _isBest, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        set => SetProperty(ref _isTesting, value);
    }

    public void ApplyScore(StrategyScore? score)
    {
        if (score is null)
        {
            Score = 0;
            TestedAt = string.Empty;
            HasScore = false;
            return;
        }

        Score = score.Score;
        TestedAt = score.TestedAt;
        HasScore = true;
    }
}
