using Microsoft.Win32;

namespace RDG.Zapret.Services;

/// <summary>Finds zapret installations in typical locations without touching the disk recursively.</summary>
public static class ZapretLocator
{
    public static bool LooksLikeZapret(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return false;

        try
        {
            var root = Path.GetFullPath(folder);
            return new[]
            {
                Path.Combine(root, "bin", "winws.exe"),
                Path.Combine(root, "bin", "winws2.exe"),
                Path.Combine(root, "winws.exe"),
                Path.Combine(root, "winws2.exe"),
                Path.Combine(root, "zapret-winws", "winws.exe")
            }.Any(File.Exists);
        }
        catch
        {
            return false;
        }
    }

    public static string? Find(string? preferred)
    {
        foreach (var candidate in CandidateFolders(preferred))
        {
            if (LooksLikeZapret(candidate))
                return candidate;
        }

        foreach (var root in CandidateRoots())
        {
            try
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (var direct in Directory.EnumerateDirectories(root))
                {
                    if (!Path.GetFileName(direct).Contains("zapret", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (LooksLikeZapret(direct))
                        return direct;

                    try
                    {
                        foreach (var nested in Directory.EnumerateDirectories(direct))
                        {
                            if (LooksLikeZapret(nested))
                                return nested;
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }

    public static IEnumerable<string> CandidateFolders(string? preferred)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                var full = Path.GetFullPath(path.Trim().Trim('"'));
                if (seen.Add(full))
                    result.Add(full);
            }
            catch
            {
                // ignored
            }
        }

        Add(preferred);

        var registered = FromServiceRegistry();
        if (registered is not null)
            Add(registered);

        Add(AppContext.BaseDirectory);
        Add(Path.GetDirectoryName(AppContext.BaseDirectory));
        Add(Environment.CurrentDirectory);
        Add(@"C:\zapret");
        Add(@"C:\zapret-discord-youtube");
        Add(@"C:\zapret-win-bundle");
        Add(@"D:\zapret");
        Add(ZapretDownloadService.ManagedInstallPath);

        foreach (var root in CandidateRoots())
            Add(root);

        return result;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return @"C:\";
        yield return @"D:\";
    }

    /// <summary>If zapret was installed as a Windows service, its ImagePath points at the real installation.</summary>
    private static string? FromServiceRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
            if (key?.GetValue("ImagePath") is not string imagePath || string.IsNullOrWhiteSpace(imagePath))
                return null;

            imagePath = imagePath.Trim();
            string exePath;
            if (imagePath.StartsWith("\"", StringComparison.Ordinal))
            {
                var closing = imagePath.IndexOf('"', 1);
                exePath = closing > 1 ? imagePath[1..closing] : imagePath.Trim('"');
            }
            else
            {
                var space = imagePath.IndexOf(' ');
                exePath = space > 0 ? imagePath[..space] : imagePath;
            }

            exePath = Environment.ExpandEnvironmentVariables(exePath);
            if (!File.Exists(exePath))
                return null;

            var directory = Path.GetDirectoryName(exePath);
            if (directory is null)
                return null;

            return Path.GetFileName(directory).Equals("bin", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(directory)
                : directory;
        }
        catch
        {
            return null;
        }
    }
}
