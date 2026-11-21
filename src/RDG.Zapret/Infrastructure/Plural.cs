namespace RDG.Zapret.Infrastructure;

public static class Plural
{
    /// <summary>Russian plural form: 1 стратегия, 2 стратегии, 5 стратегий.</summary>
    public static string Ru(int count, string one, string few, string many)
    {
        var n = Math.Abs(count) % 100;
        if (n is >= 11 and <= 14)
            return many;

        return (n % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many
        };
    }
}
