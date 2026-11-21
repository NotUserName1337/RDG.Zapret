namespace RDG.Zapret.Infrastructure;

/// <summary>Sorts "ALT2" before "ALT10" like Windows Explorer does.</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var i = 0;
        var j = 0;

        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                var numberX = x.Substring(startX, i - startX).TrimStart('0');
                var numberY = y.Substring(startY, j - startY).TrimStart('0');

                var cmp = numberX.Length.CompareTo(numberY.Length);
                if (cmp == 0)
                    cmp = string.CompareOrdinal(numberX, numberY);
                if (cmp != 0)
                    return cmp;
            }
            else
            {
                var a = char.ToUpperInvariant(x[i]);
                var b = char.ToUpperInvariant(y[j]);
                if (a != b)
                    return a.CompareTo(b);
                i++;
                j++;
            }
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
