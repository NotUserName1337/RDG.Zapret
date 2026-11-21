using System.Text;
using System.Windows;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

public sealed class LogService
{
    private const int MaxEntries = 2500;
    private readonly object _fileLock = new();
    private readonly ObservableCollection<LogEntry> _entries = new();
    private readonly ReadOnlyObservableCollection<LogEntry> _readOnlyEntries;
    private string _currentFile;
    private string _currentDate;

    public LogService()
    {
        DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RDG.Zapret",
            "logs");
        Directory.CreateDirectory(DirectoryPath);
        _currentDate = DateTime.Now.ToString("yyyy-MM-dd");
        _currentFile = Path.Combine(DirectoryPath, $"app-{_currentDate}.log");
        _readOnlyEntries = new ReadOnlyObservableCollection<LogEntry>(_entries);
    }

    public string DirectoryPath { get; }
    public ReadOnlyObservableCollection<LogEntry> Entries => _readOnlyEntries;
    public string CurrentFile => _currentFile;

    public event EventHandler<LogEntry>? EntryAdded;

    public void Debug(string source, string message) => Add(LogLevel.Debug, source, message);
    public void Info(string source, string message) => Add(LogLevel.Info, source, message);
    public void Success(string source, string message) => Add(LogLevel.Success, source, message);
    public void Warn(string source, string message) => Add(LogLevel.Warn, source, message);
    public void Error(string source, string message) => Add(LogLevel.Error, source, message);

    public void Add(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, source, message);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => AddCore(entry)));
            return;
        }

        AddCore(entry);
    }

    private void AddCore(LogEntry entry)
    {
        _entries.Add(entry);
        if (_entries.Count > MaxEntries)
        {
            for (var i = 0; i < 250 && _entries.Count > 0; i++)
                _entries.RemoveAt(0);
        }

        EntryAdded?.Invoke(this, entry);

        try
        {
            var date = DateTime.Now.ToString("yyyy-MM-dd");
            if (date != _currentDate)
            {
                _currentDate = date;
                _currentFile = Path.Combine(DirectoryPath, $"app-{date}.log");
            }

            lock (_fileLock)
            {
                File.AppendAllText(_currentFile, entry.LineText + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public void Clear()
    {
        _entries.Clear();
        Info("app", "Журнал очищен");
    }
}
