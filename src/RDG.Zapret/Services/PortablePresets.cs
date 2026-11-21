using System.Text;

namespace RDG.Zapret.Services;

/// <summary>
/// Detects ready-made .bat/.cmd strategies placed next to RDG.Zapret.exe.
/// This enables a portable distribution folder: exe + strategies (+ optional zapret core).
/// </summary>
public static class PortablePresets
{
    public static IReadOnlyList<string> Find(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<string>();

        try
        {
            var files = Directory.EnumerateFiles(directory, "*.bat", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(directory, "*.cmd", SearchOption.TopDirectoryOnly))
                .Where(file => !Path.GetFileName(file).StartsWith("service", StringComparison.OrdinalIgnoreCase))
                .Where(IsZapretPreset)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return files;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static bool IsDirectoryWritable(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return false;

            var probe = Path.Combine(directory, ".rdg-write-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
                // opening and closing is enough
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int CopyTo(IReadOnlyList<string> presets, string targetDirectory, LogService? log = null)
    {
        if (presets.Count == 0)
            return 0;

        try
        {
            Directory.CreateDirectory(targetDirectory);
        }
        catch
        {
            return 0;
        }

        var copied = 0;
        foreach (var preset in presets)
        {
            try
            {
                var destination = Path.Combine(targetDirectory, Path.GetFileName(preset));
                if (string.Equals(Path.GetFullPath(preset), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    continue;

                File.Copy(preset, destination, overwrite: true);
                copied++;
            }
            catch (Exception ex)
            {
                log?.Warn("portable", $"Не удалось скопировать стратегию {Path.GetFileName(preset)}: {ex.Message}");
            }
        }

        return copied;
    }

    private static bool IsZapretPreset(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[8192];
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                return false;

            var text = new string(buffer, 0, read);
            return text.Contains("winws", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
