using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;
using RDG.Zapret.Services;

namespace RDG.Zapret.ViewModels;

public sealed class LogsViewModel : ObservableObject
{
    private readonly LogService _log;
    private LogFilterOption _selectedFilter;
    private string _searchText = string.Empty;
    private bool _autoScroll = true;
    private int _visibleCount;
    private bool _isEmpty = true;

    public LogsViewModel(LogService log)
    {
        _log = log;

        Filters = new ObservableCollection<LogFilterOption>
        {
            new() { Key = "all", Label = "Все" },
            new() { Key = "info", Label = "События" },
            new() { Key = "warn", Label = "Предупреждения" },
            new() { Key = "error", Label = "Ошибки" }
        };
        _selectedFilter = Filters[0];

        View = CollectionViewSource.GetDefaultView(_log.Entries);
        View.Filter = FilterEntry;

        if (View is INotifyCollectionChanged collection)
            collection.CollectionChanged += (_, _) => UpdateCounts();

        ClearCommand = new RelayCommand(() => _log.Clear());
        CopyCommand = new RelayCommand(CopyVisible, () => VisibleCount > 0);
        OpenFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(_log.DirectoryPath));
        OpenFileCommand = new RelayCommand(() => ShellHelper.SelectFile(_log.CurrentFile));

        UpdateCounts();
    }

    public ICollectionView View { get; }
    public ObservableCollection<LogFilterOption> Filters { get; }

    public RelayCommand ClearCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenFileCommand { get; }

    public LogFilterOption SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (value is null || !SetProperty(ref _selectedFilter, value))
                return;
            View.Refresh();
            UpdateCounts();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
                return;
            View.Refresh();
            UpdateCounts();
        }
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public int VisibleCount
    {
        get => _visibleCount;
        private set
        {
            if (SetProperty(ref _visibleCount, value))
            {
                OnPropertyChanged(nameof(CountText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetProperty(ref _isEmpty, value);
    }

    public string CountText => $"{VisibleCount} записей";
    public string LogDirectory => _log.DirectoryPath;

    private bool FilterEntry(object item)
    {
        if (item is not LogEntry entry)
            return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var query = SearchText.Trim();
            if (!entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.Source.Contains(query, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return SelectedFilter.Key switch
        {
            "info" => entry.Level is LogLevel.Info or LogLevel.Success,
            "warn" => entry.Level == LogLevel.Warn,
            "error" => entry.Level == LogLevel.Error,
            _ => true
        };
    }

    private void UpdateCounts()
    {
        var count = View.Cast<object>().Count();
        VisibleCount = count;
        IsEmpty = count == 0;
    }

    private void CopyVisible()
    {
        var lines = View.Cast<LogEntry>().Select(e => e.LineText);
        ShellHelper.CopyText(string.Join(Environment.NewLine, lines));
    }
}
