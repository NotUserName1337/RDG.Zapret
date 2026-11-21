namespace RDG.Zapret.Models;

public sealed record BannerLink(string Label, string Url);

/// <summary>Small promotional banner shown on the dashboard. Managed from admin.php → Zapret.</summary>
public sealed class BannerInfo
{
    public bool Enabled { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public List<BannerLink> Links { get; set; } = new();
    public string Updated { get; set; } = string.Empty;

    /// <summary>Local file path or pack:// URI used by WPF.</summary>
    public string LocalImagePath { get; set; } = string.Empty;
}
