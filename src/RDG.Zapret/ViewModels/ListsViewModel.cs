using System.Windows.Input;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;
using RDG.Zapret.Services;

namespace RDG.Zapret.ViewModels;

public sealed class ListsViewModel : ObservableObject, IPageActivation
{
    private readonly PresetScanner _scanner;
    private readonly WinwsController _winws;
    private readonly UserListService _lists;
    private readonly OperationCoordinator _operations;
    private readonly LogService _log;
    private readonly Action<string, bool> _toast;
    private bool _needsRestart;
    private string _bulkText = string.Empty;
    private ListBulkOption _selectedBulkKind;

    public ListsViewModel(
        PresetScanner scanner,
        WinwsController winws,
        UserListService lists,
        OperationCoordinator operations,
        LogService log,
        Action<string, bool> toast)
    {
        _scanner = scanner;
        _winws = winws;
        _lists = lists;
        _operations = operations;
        _log = log;
        _toast = toast;

        BulkKinds = new ObservableCollection<ListBulkOption>
        {
            new(UserListKind.DomainBypass, "Сайты в обход"),
            new(UserListKind.DomainDirect, "Сайты напрямую"),
            new(UserListKind.IpBypass, "IP в обход"),
            new(UserListKind.IpDirect, "IP напрямую")
        };
        _selectedBulkKind = BulkKinds[0];

        Sections = new ObservableCollection<ListSectionViewModel>
        {
            new(this, UserListKind.DomainBypass, "Сайты в обход",
                "Эти домены принудительно пойдут через zapret.", "example.com"),
            new(this, UserListKind.DomainDirect, "Сайты напрямую",
                "Эти домены не будут обрабатываться обходом.", "bank.ru"),
            new(this, UserListKind.IpBypass, "IP в обход",
                "IP или подсеть, которые нужно обрабатывать обходом.", "1.2.3.4 или 10.0.0.0/8"),
            new(this, UserListKind.IpDirect, "IP напрямую",
                "IP или подсеть, которые нельзя трогать обходом.", "192.168.0.0/16")
        };

        OpenFolderCommand = new RelayCommand(OpenFolder, () => _scanner.Current is not null);
        RefreshCommand = new RelayCommand(() => Reload(), () => _scanner.Current is not null);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => NeedsRestart && _winws.IsRunning);
        BulkAddCommand = new RelayCommand(AddBulk, () => !string.IsNullOrWhiteSpace(BulkText));
        BulkImportCommand = new RelayCommand(ImportBulk, () => _scanner.Current is not null);
        Reload();
    }

    public ObservableCollection<ListSectionViewModel> Sections { get; }
    public ObservableCollection<ListBulkOption> BulkKinds { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RestartCommand { get; }
    public RelayCommand BulkAddCommand { get; }
    public RelayCommand BulkImportCommand { get; }

    public string BulkText
    {
        get => _bulkText;
        set
        {
            if (SetProperty(ref _bulkText, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public ListBulkOption SelectedBulkKind
    {
        get => _selectedBulkKind;
        set
        {
            if (value is not null)
                SetProperty(ref _selectedBulkKind, value);
        }
    }

    public bool NeedsRestart
    {
        get => _needsRestart;
        private set
        {
            if (SetProperty(ref _needsRestart, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasInstallation => _scanner.Current is not null;
    public string EngineHint => !HasInstallation
        ? "Папка zapret не выбрана"
        : $"{_scanner.Engine.DisplayName()} · {_lists.ListsDirectory(_scanner.Current!.RootPath, _scanner.Engine)}";

    public Task OnActivatedAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    internal void Add(ListSectionViewModel section, UserListKind kind, string value)
    {
        var root = _scanner.Current?.RootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            _toast("Сначала укажите папку zapret", true);
            return;
        }

        var result = _lists.Add(root, _scanner.Engine, kind, value);
        _toast(result.Message, !result.Success);
        if (!result.Success)
            return;

        section.Input = string.Empty;
        Reload(keepInputs: true);
        NeedsRestart = true;
        _log.Info("lists", $"{section.Title}: + {value}");
    }

    internal void Remove(ListSectionViewModel section, UserListKind kind, string value)
    {
        var root = _scanner.Current?.RootPath;
        if (string.IsNullOrWhiteSpace(root))
            return;

        var result = _lists.Remove(root, _scanner.Engine, kind, value);
        _toast(result.Message, !result.Success);
        if (!result.Success)
            return;

        Reload(keepInputs: true);
        NeedsRestart = true;
        _log.Info("lists", $"{section.Title}: − {value}");
    }

    private void AddBulk()
    {
        var root = _scanner.Current?.RootPath;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(BulkText))
            return;

        var result = _lists.AddMany(root, _scanner.Engine, SelectedBulkKind.Kind, BulkText);
        _toast(result.Message, !result.Success);
        if (!result.Success)
            return;

        BulkText = string.Empty;
        Reload(keepInputs: true);
        NeedsRestart = true;
        _log.Info("lists", $"{SelectedBulkKind.Label}: массовое добавление");
    }

    private void ImportBulk()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импорт списка",
            Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            BulkText = File.ReadAllText(dialog.FileName);
            _toast("Файл загружен. Проверьте список и нажмите «Добавить всё».", false);
        }
        catch (Exception ex)
        {
            _toast("Не удалось прочитать файл: " + ex.Message, true);
        }
    }

    private void Reload(bool keepInputs = false)
    {
        var root = _scanner.Current?.RootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            foreach (var section in Sections)
            {
                section.Entries.Clear();
                section.NotifyEntriesChanged();
            }
            OnPropertyChanged(nameof(HasInstallation), nameof(EngineHint));
            return;
        }

        _lists.SyncAll(root);
        foreach (var section in Sections)
        {
            var input = keepInputs ? section.Input : string.Empty;
            section.Entries.Clear();
            foreach (var entry in _lists.Read(root, _scanner.Engine, section.Kind))
                section.Entries.Add(entry);
            section.Input = input;
            section.NotifyEntriesChanged();
        }

        OnPropertyChanged(nameof(HasInstallation), nameof(EngineHint));
    }

    private void OpenFolder()
    {
        var root = _scanner.Current?.RootPath;
        if (string.IsNullOrWhiteSpace(root))
            return;
        var dir = _lists.ListsDirectory(root, _scanner.Engine);
        if (Directory.Exists(dir))
            ShellHelper.OpenFolder(dir);
    }

    private async Task RestartAsync()
    {
        var preset = _winws.ActivePreset;
        if (preset is null)
        {
            _toast("Обход запущен внешней программой — перезапустите его вручную.", true);
            return;
        }

        if (!_operations.TryBegin("lists-restart"))
        {
            _toast("Дождитесь завершения текущей операции", true);
            return;
        }

        try
        {
            var outcome = await _winws.StartAsync(preset);
            if (outcome.Success)
            {
                NeedsRestart = false;
                _toast("Обход перезапущен, списки применены", false);
            }
            else
            {
                _toast(outcome.Error ?? "Не удалось перезапустить обход", true);
            }
        }
        finally
        {
            _operations.End("lists-restart");
        }
    }
}

public sealed class ListBulkOption
{
    public ListBulkOption(UserListKind kind, string label)
    {
        Kind = kind;
        Label = label;
    }

    public UserListKind Kind { get; }
    public string Label { get; }
}

public sealed class ListSectionViewModel : ObservableObject
{
    private readonly ListsViewModel _owner;
    private string _input = string.Empty;

    internal ListSectionViewModel(ListsViewModel owner, UserListKind kind, string title, string hint, string placeholder)
    {
        _owner = owner;
        Kind = kind;
        Title = title;
        Hint = hint;
        Placeholder = placeholder;
        AddCommand = new RelayCommand(Add, () => !string.IsNullOrWhiteSpace(Input));
        RemoveCommand = new RelayCommand(obj => Remove(obj as string));
    }

    public UserListKind Kind { get; }
    public string Title { get; }
    public string Hint { get; }
    public string Placeholder { get; }
    public ObservableCollection<string> Entries { get; } = new();
    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public string Input
    {
        get => _input;
        set
        {
            if (SetProperty(ref _input, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsEmpty => Entries.Count == 0;

    public void NotifyEntriesChanged() => OnPropertyChanged(nameof(IsEmpty));

    private void Add() => _owner.Add(this, Kind, Input);

    private void Remove(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _owner.Remove(this, Kind, value);
    }
}
