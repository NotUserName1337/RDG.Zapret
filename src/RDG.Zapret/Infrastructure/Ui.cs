using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace RDG.Zapret.Infrastructure;

public static class Ui
{
    public static void Invoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
            return;
        }

        action();
    }
}

public static class ShellHelper
{
    public static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // ignored
        }
    }

    public static void SelectFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // ignored
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // ignored
        }
    }

    public static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // clipboard may be locked by another process
        }
    }
}

public interface IPageActivation
{
    Task OnActivatedAsync();
}
