using System.Windows.Input;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;
using RDG.Zapret.Services;

namespace RDG.Zapret.ViewModels;

/// <summary>Compact VPN banner shown above the service list on the dashboard.</summary>
public sealed class BannerViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly BannerService _service;
    private readonly LogService _log;
    private BannerInfo _info = CreateDefault();
    private DateTime _lastLoad = DateTime.MinValue;
    private bool _loading;

    public BannerViewModel(BannerService service, LogService log)
    {
        _service = service;
        _log = log;

        OpenLink1Command = new RelayCommand(() => Open(Link1Url), () => HasLink1);
        OpenLink2Command = new RelayCommand(() => Open(Link2Url), () => HasLink2);
    }

    public RelayCommand OpenLink1Command { get; }
    public RelayCommand OpenLink2Command { get; }

    public bool IsVisible =>
        _info.Enabled &&
        !string.IsNullOrWhiteSpace(_info.Title) &&
        _info.Links.Count > 0;

    public string Title => _info.Title;
    public string Text => _info.Text;
    public bool HasText => !string.IsNullOrWhiteSpace(_info.Text);

    public string ImagePath =>
        string.IsNullOrWhiteSpace(_info.LocalImagePath)
            ? BannerService.DefaultImage
            : _info.LocalImagePath;

    public bool HasImage => !string.IsNullOrWhiteSpace(ImagePath);

    public string Link1Label => _info.Links.Count > 0 ? _info.Links[0].Label : string.Empty;
    public string Link1Url => _info.Links.Count > 0 ? _info.Links[0].Url : string.Empty;
    public bool HasLink1 => _info.Links.Count > 0 && !string.IsNullOrWhiteSpace(Link1Url);

    public string Link2Label => _info.Links.Count > 1 ? _info.Links[1].Label : string.Empty;
    public string Link2Url => _info.Links.Count > 1 ? _info.Links[1].Url : string.Empty;
    public bool HasLink2 => _info.Links.Count > 1 && !string.IsNullOrWhiteSpace(Link2Url);

    public async Task RefreshAsync(bool force = false)
    {
        if (_loading)
            return;

        if (!force && DateTime.Now - _lastLoad < RefreshInterval)
            return;

        _loading = true;
        try
        {
            var info = await _service.LoadAsync(CancellationToken.None);
            if (info is not null)
            {
                _info = info;
                _log.Debug("banner", $"Баннер обновлён: {info.Title}");
            }

            _lastLoad = DateTime.Now;
            Apply();
        }
        catch (Exception ex)
        {
            _log.Debug("banner", "Ошибка обновления баннера: " + ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Apply()
    {
        OnPropertyChanged(
            nameof(IsVisible),
            nameof(Title),
            nameof(Text),
            nameof(HasText),
            nameof(ImagePath),
            nameof(HasImage),
            nameof(Link1Label),
            nameof(Link1Url),
            nameof(HasLink1),
            nameof(Link2Label),
            nameof(Link2Url),
            nameof(HasLink2));

        CommandManager.InvalidateRequerySuggested();
    }

    private static void Open(string url)
    {
        if (!string.IsNullOrWhiteSpace(url))
            ShellHelper.OpenUrl(url);
    }

    private static BannerInfo CreateDefault() => new()
    {
        // The repository ships no promotional content. A configured update
        // host may provide banner.json with its own title, image and links.
        Enabled = false,
        Title = string.Empty,
        Text = string.Empty
    };
}
