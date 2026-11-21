using System.Text;
using System.Text.RegularExpressions;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

public sealed record ParseOutcome(StrategyPreset? Preset, string? Error);

/// <summary>
/// Converts a zapret .bat/.cmd preset into a direct winws.exe command line.
/// The batch scripts only prepare variables and call "start ... winws.exe ARGS";
/// running the engine directly gives us clean logs and full process control.
/// </summary>
public static class PresetParser
{
    private static readonly Regex WinwsToken = new(
        "\"(?<quoted>[^\"]*winws2?\\.exe)\"|(?<bare>[^\\s\"]*winws2?\\.exe)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VariablePattern = new("%([A-Za-z_][A-Za-z0-9_]*)%", RegexOptions.Compiled);

    private static readonly Regex SetLine = new(
        "^\\s*set\\s+\"(?<n1>[^=\"]+)=(?<v1>.*)\"\\s*$|^\\s*set\\s+(?<n2>[^=\\s]+)=(?<v2>.*?)\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CdLine = new(
        "^\\s*cd\\s+/d\\s+(?<path>.+?)\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ParseOutcome Parse(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(dir))
                return new ParseOutcome(null, "не удалось определить папку пресета");

            var dirSlash = dir.EndsWith("\\", StringComparison.Ordinal) ? dir : dir + "\\";
            var baseName = Path.GetFileNameWithoutExtension(filePath);

            var text = ReadText(filePath);
            var lines = JoinContinuations(text);
            var variables = CollectVariables(lines, dirSlash, baseName);
            ApplyGameFilter(variables, dir);

            var commandLine =
                lines.FirstOrDefault(l => WinwsToken.IsMatch(l) && l.TrimStart().StartsWith("start", StringComparison.OrdinalIgnoreCase))
                ?? lines.FirstOrDefault(l => WinwsToken.IsMatch(l) && !l.TrimStart().StartsWith("echo", StringComparison.OrdinalIgnoreCase));

            if (commandLine is null)
                return new ParseOutcome(null, "в файле нет команды запуска winws.exe");

            var match = WinwsToken.Match(commandLine);
            var engineRaw = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["bare"].Value;

            var enginePath = Expand(engineRaw.Trim('"', ' '), variables, dirSlash, baseName).Trim('"', ' ');
            if (!Path.IsPathRooted(enginePath))
            {
                var direct = Path.GetFullPath(Path.Combine(dir, enginePath));
                enginePath = File.Exists(direct)
                    ? direct
                    : Path.Combine(dir, "bin", Path.GetFileName(enginePath));
            }

            if (!File.Exists(enginePath))
                return new ParseOutcome(null, $"не найден движок {Path.GetFileName(enginePath)}");

            var argsRaw = commandLine[(match.Index + match.Length)..];
            var args = Expand(argsRaw, variables, dirSlash, baseName).Trim();
            args = args.Replace(" ^", " ").Trim();

            if (string.IsNullOrWhiteSpace(args))
                return new ParseOutcome(null, "команда запуска без аргументов");

            var unknownVariable = VariablePattern.Match(args);
            if (unknownVariable.Success)
                return new ParseOutcome(null, $"не раскрыта переменная {unknownVariable.Value}");

            args = Regex.Replace(args, @"\s{2,}", " ").Trim();

            // winws2 accepts response files as @"file". Older generated .cmd
            // presets used "@file" (quote before @), which winws2 rejects with
            // "failed to split command line options from file".
            if (args.StartsWith("\"@", StringComparison.Ordinal) &&
                args.IndexOf('"', 2) == args.Length - 1)
            {
                args = "@\"" + args.Substring(2, args.Length - 3) + "\"";
            }

            var workingDir = DetectWorkingDirectory(lines, variables, dirSlash, baseName)
                             ?? Path.GetDirectoryName(enginePath)
                             ?? dir;

            var info = Describe(filePath);
            var preset = new StrategyPreset
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                DisplayName = info.DisplayName,
                Family = info.Family,
                Tag = info.Tag,
                IsExperimental = info.IsExperimental,
                EnginePath = enginePath,
                Arguments = args,
                WorkingDirectory = workingDir
            };

            return new ParseOutcome(preset, null);
        }
        catch (Exception ex)
        {
            return new ParseOutcome(null, ex.Message);
        }
    }

    private static string ReadText(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Contains('\uFFFD'))
            text = File.ReadAllText(path, Encoding.Default);
        return text;
    }

    private static List<string> JoinContinuations(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        foreach (var raw in rawLines)
        {
            var line = raw.TrimEnd();

            // A blank line inside a caret continuation is ignored by cmd.exe;
            // keep accumulating the same command instead of ending it early.
            if (line.Length == 0 && current.Length > 0)
                continue;

            if (line.EndsWith("^", StringComparison.Ordinal))
            {
                current.Append(line, 0, line.Length - 1);
                current.Append(' ');
                continue;
            }

            current.Append(line);
            result.Add(current.ToString());
            current.Clear();
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private static Dictionary<string, string> CollectVariables(IEnumerable<string> lines, string dirSlash, string baseName)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var m = SetLine.Match(line);
            if (!m.Success)
                continue;

            var name = m.Groups["n1"].Success ? m.Groups["n1"].Value : m.Groups["n2"].Value;
            var value = m.Groups["n1"].Success ? m.Groups["v1"].Value : m.Groups["v2"].Value;
            name = name.Trim();
            if (name.Length == 0)
                continue;

            value = ReplaceSpecialFolders(value, dirSlash, baseName);
            value = ExpandKnownVariables(value, variables);
            value = Environment.ExpandEnvironmentVariables(value);
            variables[name] = value;
        }

        return variables;
    }

    private static void ApplyGameFilter(IDictionary<string, string> variables, string dir)
    {
        // service.bat exports these variables before presets run. Replicate its logic
        // so presets that use %GameFilterTCP% / %GameFilterUDP% work when launched directly.
        var tcp = "12";
        var udp = "12";
        var game = "12";

        var flagFile = Path.Combine(dir, "utils", "game_filter.enabled");
        if (File.Exists(flagFile))
        {
            try
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(flagFile))
                {
                    var idx = line.IndexOf('=');
                    if (idx <= 0)
                        continue;
                    values[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                }

                var mode = values.TryGetValue("mode", out var m) ? m.ToLowerInvariant() : "disabled";
                var tcpRange = SanitizePorts(values.TryGetValue("tcp", out var t) ? t : "1024-65535");
                var udpRange = SanitizePorts(values.TryGetValue("udp", out var u) ? u : "1024-65535");

                if (mode == "all")
                {
                    tcp = tcpRange;
                    udp = udpRange;
                    game = tcpRange;
                }
                else if (mode == "tcp")
                {
                    tcp = tcpRange;
                    game = tcpRange;
                }
                else if (mode == "udp")
                {
                    udp = udpRange;
                    game = udpRange;
                }
            }
            catch
            {
                // fall back to defaults
            }
        }

        variables["GameFilterTCP"] = tcp;
        variables["GameFilterUDP"] = udp;
        variables["GameFilter"] = game;
    }

    private static string SanitizePorts(string value)
    {
        value = value.Replace(" ", string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return "1024-65535";

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Regex.IsMatch(part, @"^[1-9][0-9]{0,4}(-[1-9][0-9]{0,4})?$"))
                return "1024-65535";
            var pieces = part.Split('-');
            if (pieces.Length == 2 &&
                (!int.TryParse(pieces[0], out var a) || !int.TryParse(pieces[1], out var b) || a > b || b > 65535))
                return "1024-65535";
        }

        return value;
    }

    private static string? DetectWorkingDirectory(
        IEnumerable<string> lines,
        IDictionary<string, string> variables,
        string dirSlash,
        string baseName)
    {
        string? last = null;
        foreach (var line in lines)
        {
            var m = CdLine.Match(line);
            if (!m.Success)
                continue;
            var value = Expand(m.Groups["path"].Value.Trim('"', ' '), variables, dirSlash, baseName);
            if (Directory.Exists(value))
                last = value;
        }

        return last;
    }

    private static string Expand(
        string input,
        IDictionary<string, string> variables,
        string dirSlash,
        string baseName)
    {
        var result = ReplaceSpecialFolders(input, dirSlash, baseName);

        for (var pass = 0; pass < 8; pass++)
        {
            var replaced = false;
            result = VariablePattern.Replace(result, m =>
            {
                var name = m.Groups[1].Value;
                if (!variables.TryGetValue(name, out var value))
                    return m.Value;
                replaced = true;
                return value;
            });

            if (!replaced)
                break;
        }

        return Environment.ExpandEnvironmentVariables(result);
    }

    private static string ReplaceSpecialFolders(string value, string dirSlash, string baseName)
    {
        return value
            .Replace("%~dp0", dirSlash, StringComparison.OrdinalIgnoreCase)
            .Replace("%~n0", baseName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExpandKnownVariables(string value, IReadOnlyDictionary<string, string> variables)
    {
        for (var pass = 0; pass < 6; pass++)
        {
            var replaced = false;
            value = VariablePattern.Replace(value, m =>
            {
                if (!variables.TryGetValue(m.Groups[1].Value, out var known))
                    return m.Value;
                replaced = true;
                return known;
            });

            if (!replaced)
                break;
        }

        return value;
    }

    private static (string DisplayName, string Family, string Tag, bool IsExperimental) Describe(string filePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(filePath);

        // Files copied twice often end with " (2)". Keep it in the display name
        // but do not let it become the main variant tag.
        var duplicateSuffix = string.Empty;
        var duplicate = Regex.Match(baseName, @"\s+\((?<n>\d+)\)$");
        if (duplicate.Success && baseName[..duplicate.Index].Contains('('))
        {
            duplicateSuffix = " (" + duplicate.Groups["n"].Value + ")";
            baseName = baseName[..duplicate.Index];
        }

        if (Regex.IsMatch(baseName, @"^\d+$"))
            return ($"Стратегия {baseName}", "Стратегия", "Стратегия", false);

        // Community kinlay0 presets are installed as "kinlay0 (DOUBLE FAKE).bat".
        var community = Regex.Match(baseName, @"^kinlay0\s*\((?<v>.+)\)$", RegexOptions.IgnoreCase);
        if (community.Success)
        {
            var label = community.Groups["v"].Value.Trim();
            var experimentalCommunity = label.Contains("EXP", StringComparison.OrdinalIgnoreCase) ||
                                        label.Contains("CIRCULAR", StringComparison.OrdinalIgnoreCase);
            return ($"Kinlay0 · {label}{duplicateSuffix}", "Kinlay0", label, experimentalCommunity);
        }

        var family = baseName.StartsWith("general", StringComparison.OrdinalIgnoreCase)
            ? "General"
            : Prettify(baseName);

        var variant = string.Empty;
        var match = Regex.Match(baseName, @"\((?<v>[^)]+)\)\s*$");
        if (match.Success)
            variant = match.Groups["v"].Value.Trim();

        var experimental = variant.Contains("EXP", StringComparison.OrdinalIgnoreCase);
        var tag = NormalizeTag(variant);
        var display = string.IsNullOrEmpty(tag) ? family : $"{family} · {tag}";
        display += duplicateSuffix;

        return (display, family, string.IsNullOrEmpty(tag) ? "Базовый" : tag, experimental);
    }

    private static string NormalizeTag(string variant)
    {
        if (string.IsNullOrWhiteSpace(variant))
            return string.Empty;

        variant = Regex.Replace(variant, "\\s+", " ").Trim();

        var alt = Regex.Match(variant, @"^ALT\s*(?<n>\d*)$", RegexOptions.IgnoreCase);
        if (alt.Success)
            return alt.Groups["n"].Value.Length == 0 ? "ALT" : "ALT " + alt.Groups["n"].Value;

        if (variant.Equals("EXP", StringComparison.OrdinalIgnoreCase))
            return "Experimental";

        var fakeAlt = Regex.Match(variant, @"^FAKE TLS AUTO ALT\s*(?<n>\d*)$", RegexOptions.IgnoreCase);
        if (fakeAlt.Success)
            return fakeAlt.Groups["n"].Value.Length == 0 ? "Fake TLS Auto ALT" : "Fake TLS Auto ALT " + fakeAlt.Groups["n"].Value;

        if (variant.Equals("FAKE TLS AUTO", StringComparison.OrdinalIgnoreCase))
            return "Fake TLS Auto";

        var simpleAlt = Regex.Match(variant, @"^SIMPLE FAKE ALT\s*(?<n>\d*)$", RegexOptions.IgnoreCase);
        if (simpleAlt.Success)
            return simpleAlt.Groups["n"].Value.Length == 0 ? "Simple Fake ALT" : "Simple Fake ALT " + simpleAlt.Groups["n"].Value;

        if (variant.Equals("SIMPLE FAKE", StringComparison.OrdinalIgnoreCase))
            return "Simple Fake";

        return variant;
    }

    private static string Prettify(string baseName)
    {
        var words = baseName.Replace('_', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var text = string.Join(' ', words.Select(w =>
            w.Length <= 3 && w.All(char.IsUpper) ? w : char.ToUpperInvariant(w[0]) + w[1..]));
        return string.IsNullOrWhiteSpace(text) ? baseName : text;
    }
}
