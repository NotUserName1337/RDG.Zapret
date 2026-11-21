namespace RDG.Zapret.Models;

public enum ZapretEngine
{
    Zapret,
    Zapret2
}

public static class ZapretEngineExtensions
{
    public const string ZapretId = "zapret";
    public const string Zapret2Id = "zapret2";

    public static string Id(this ZapretEngine engine) =>
        engine == ZapretEngine.Zapret2 ? Zapret2Id : ZapretId;

    public static string DisplayName(this ZapretEngine engine) =>
        engine == ZapretEngine.Zapret2 ? "zapret2" : "zapret";

    public static string EngineFile(this ZapretEngine engine) =>
        engine == ZapretEngine.Zapret2 ? "winws2.exe" : "winws.exe";

    public static string Description(this ZapretEngine engine) =>
        engine == ZapretEngine.Zapret2
            ? "Новое ядро с Lua-стратегиями (winws2.exe)"
            : "Классическое ядро zapret (winws.exe)";

    public static ZapretEngine Parse(string? id) =>
        string.Equals(id, Zapret2Id, StringComparison.OrdinalIgnoreCase)
            ? ZapretEngine.Zapret2
            : ZapretEngine.Zapret;
}
