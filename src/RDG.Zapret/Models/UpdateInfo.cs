namespace RDG.Zapret.Models;

/// <summary>Manifest published at /zapret/version.json.</summary>
public sealed record UpdateInfo(
    string Version,
    string DownloadUrl,
    string Sha256,
    long Size,
    string Notes,
    string Published);
