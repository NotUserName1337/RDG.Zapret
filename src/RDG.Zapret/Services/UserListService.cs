using System.Net;
using System.Text;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

/// <summary>
/// Edits the user list files used by both winws and winws2. Domain and IP
/// exclusions are written to the standard *-user.txt files. IP include entries
/// are stored in ipset-user.txt and mirrored into a marked section of
/// ipset-all.txt so every preset picks them up.
/// </summary>
public sealed class UserListService
{
    public const string IpSectionStart = "# >>> RDG.Zapret user IPs >>>";
    public const string IpSectionEnd = "# <<< RDG.Zapret user IPs <<<";

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "domain.example.abc",
        "203.0.113.113/32"
    };

    public string ListsDirectory(string root, ZapretEngine engine) =>
        engine == ZapretEngine.Zapret2
            ? Path.Combine(root, "zapret2", "lists")
            : Path.Combine(root, "lists");

    public string UserFile(string root, ZapretEngine engine, UserListKind kind)
    {
        var dir = ListsDirectory(root, engine);
        return kind switch
        {
            UserListKind.DomainBypass => Path.Combine(dir, "list-general-user.txt"),
            UserListKind.DomainDirect => Path.Combine(dir, "list-exclude-user.txt"),
            UserListKind.IpBypass => Path.Combine(dir, "ipset-user.txt"),
            _ => Path.Combine(dir, "ipset-exclude-user.txt")
        };
    }

    public IReadOnlyList<string> Read(string root, ZapretEngine engine, UserListKind kind)
    {
        var path = UserFile(root, engine, kind);
        if (!File.Exists(path))
            return Array.Empty<string>();

        try
        {
            return File.ReadAllLines(path, Encoding.UTF8)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                .Where(l => !Placeholders.Contains(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public UserListChange Add(string root, ZapretEngine engine, UserListKind kind, string input)
    {
        var isIp = kind is UserListKind.IpBypass or UserListKind.IpDirect;
        var value = isIp ? NormalizeIp(input) : NormalizeDomain(input);
        if (value.Length == 0)
            return new UserListChange(false, isIp ? "Введите корректный IP или подсеть." : "Введите корректный домен.");

        try
        {
            var path = UserFile(root, engine, kind);
            var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : new List<string>();

            if (lines.Any(l => l.Trim().Equals(value, StringComparison.OrdinalIgnoreCase)))
                return new UserListChange(true, "Уже добавлено.");

            lines.Add(value);
            WriteLines(path, lines);

            if (kind == UserListKind.IpBypass)
                SyncIpBypass(root, engine);

            return new UserListChange(true, "Добавлено: " + value);
        }
        catch (UnauthorizedAccessException)
        {
            return new UserListChange(false, "Нет прав на запись в список. Запустите программу от администратора.");
        }
        catch (Exception ex)
        {
            return new UserListChange(false, "Не удалось сохранить: " + ex.Message);
        }
    }

    public UserListChange AddMany(string root, ZapretEngine engine, UserListKind kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new UserListChange(false, "Вставьте домены или IP — по одному в строке.");

        var isIp = kind is UserListKind.IpBypass or UserListKind.IpDirect;
        var values = text.Split(
                new[] { '\r', '\n', ',', ';', ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(v => isIp ? NormalizeIp(v) : NormalizeDomain(v))
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (values.Count == 0)
            return new UserListChange(false, isIp ? "Не найдено ни одного корректного IP." : "Не найдено ни одного корректного домена.");

        try
        {
            var path = UserFile(root, engine, kind);
            var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : new List<string>();
            var existing = new HashSet<string>(
                lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)),
                StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (var value in values)
            {
                if (!existing.Add(value))
                    continue;
                lines.Add(value);
                added++;
            }

            if (added == 0)
                return new UserListChange(true, "Все записи уже есть в списке.");

            WriteLines(path, lines);

            if (kind == UserListKind.IpBypass)
                SyncIpBypass(root, engine);

            return new UserListChange(true,
                $"Добавлено: {added} · пропущено: {values.Count - added}");
        }
        catch (UnauthorizedAccessException)
        {
            return new UserListChange(false, "Нет прав на запись в список. Запустите программу от администратора.");
        }
        catch (Exception ex)
        {
            return new UserListChange(false, "Не удалось сохранить: " + ex.Message);
        }
    }

    public UserListChange Remove(string root, ZapretEngine engine, UserListKind kind, string value)
    {
        try
        {
            var path = UserFile(root, engine, kind);
            if (!File.Exists(path))
                return new UserListChange(true, "Запись уже удалена.");

            var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
            var removed = lines.RemoveAll(l => l.Trim().Equals(value, StringComparison.OrdinalIgnoreCase));
            WriteLines(path, lines);

            if (kind == UserListKind.IpBypass)
                SyncIpBypass(root, engine);

            return removed > 0
                ? new UserListChange(true, "Удалено: " + value)
                : new UserListChange(true, "Запись уже удалена.");
        }
        catch (UnauthorizedAccessException)
        {
            return new UserListChange(false, "Нет прав на запись в список. Запустите программу от администратора.");
        }
        catch (Exception ex)
        {
            return new UserListChange(false, "Не удалось сохранить: " + ex.Message);
        }
    }

    public void SyncAll(string root)
    {
        if (Directory.Exists(ListsDirectory(root, ZapretEngine.Zapret)))
            SyncIpBypass(root, ZapretEngine.Zapret);
        if (Directory.Exists(ListsDirectory(root, ZapretEngine.Zapret2)))
            SyncIpBypass(root, ZapretEngine.Zapret2);
    }

    private void SyncIpBypass(string root, ZapretEngine engine)
    {
        var dir = ListsDirectory(root, engine);
        var allPath = Path.Combine(dir, "ipset-all.txt");
        var user = Read(root, engine, UserListKind.IpBypass).ToList();
        if (!File.Exists(allPath))
        {
            if (user.Count == 0)
                return;
            Directory.CreateDirectory(dir);
            File.WriteAllText(allPath, string.Empty, new UTF8Encoding(false));
        }

        var lines = File.ReadAllLines(allPath, Encoding.UTF8).ToList();
        lines = RemoveIpSection(lines);

        if (user.Count > 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add(string.Empty);
            lines.Add(IpSectionStart);
            lines.AddRange(user);
            lines.Add(IpSectionEnd);
        }

        WriteLines(allPath, lines);
    }

    private static List<string> RemoveIpSection(List<string> lines)
    {
        var result = new List<string>(lines.Count);
        var skipping = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals(IpSectionStart, StringComparison.Ordinal))
            {
                skipping = true;
                continue;
            }
            if (trimmed.Equals(IpSectionEnd, StringComparison.Ordinal))
            {
                skipping = false;
                continue;
            }
            if (!skipping)
                result.Add(line);
        }
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
            result.RemoveAt(result.Count - 1);
        return result;
    }

    private static void WriteLines(string path, IEnumerable<string> lines)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private static string NormalizeDomain(string input)
    {
        var value = (input ?? string.Empty).Trim().TrimEnd('.');
        if (value.Length == 0)
            return string.Empty;

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return string.Empty;
            value = uri.Host;
        }

        if (value.StartsWith("*.", StringComparison.Ordinal))
            value = value[2..];

        value = value.Trim().TrimEnd('/');
        if (value.Length == 0 ||
            value.Any(char.IsWhiteSpace) ||
            value.Contains('/') || value.Contains(':') || value.Contains('?') || value.Contains('#'))
            return string.Empty;

        return Uri.CheckHostName(value) == UriHostNameType.Dns
            ? value.ToLowerInvariant()
            : string.Empty;
    }

    private static string NormalizeIp(string input)
    {
        var value = (input ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        var slash = value.IndexOf('/');
        var addressPart = slash >= 0 ? value[..slash] : value;
        if (!IPAddress.TryParse(addressPart, out var address))
            return string.Empty;

        if (slash < 0)
            return address.ToString();

        if (!int.TryParse(value[(slash + 1)..], out var prefix))
            return string.Empty;

        var max = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefix < 0 || prefix > max)
            return string.Empty;

        return address + "/" + prefix;
    }
}
