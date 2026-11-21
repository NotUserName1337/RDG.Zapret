namespace RDG.Zapret.Services;

/// <summary>Prevents autotune, the generator and manual tests from fighting over winws.exe.</summary>
public sealed class OperationCoordinator
{
    private readonly object _sync = new();
    private string? _owner;

    public string? Owner
    {
        get
        {
            lock (_sync)
                return _owner;
        }
    }

    public bool IsBusy => Owner is not null;

    public bool TryBegin(string owner)
    {
        lock (_sync)
        {
            if (_owner is not null)
                return false;
            _owner = owner;
            return true;
        }
    }

    public void End(string owner)
    {
        lock (_sync)
        {
            if (string.Equals(_owner, owner, StringComparison.Ordinal))
                _owner = null;
        }
    }
}
