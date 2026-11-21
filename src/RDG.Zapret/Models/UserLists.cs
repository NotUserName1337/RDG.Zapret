namespace RDG.Zapret.Models;

public enum UserListKind
{
    DomainBypass,
    DomainDirect,
    IpBypass,
    IpDirect
}

public sealed record UserListChange(bool Success, string Message);
