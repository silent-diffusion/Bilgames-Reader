using Velopack;
using Velopack.Sources;

namespace Bilgames.App.Services;

/// <summary>
/// App updates from GitHub Releases (silent-diffusion/Bilgames-Reader) via Velopack. Every call
/// here reaches the network, so callers must have the user's confirmation first.
/// </summary>
public sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/silent-diffusion/Bilgames-Reader";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    private UpdateInfo? _pending;

    /// <summary>False when running from a build folder rather than the installed app.</summary>
    public bool CanUpdate => _manager.IsInstalled;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    /// <summary>Returns the newer version's number, or null when up to date.</summary>
    public async Task<string?> CheckAsync()
    {
        if (!CanUpdate) return null;
        _pending = await _manager.CheckForUpdatesAsync();
        return _pending?.TargetFullRelease.Version.ToString();
    }

    public long PendingSize => _pending?.TargetFullRelease.Size ?? 0;

    public async Task DownloadAsync(Action<int> progress)
    {
        if (_pending is null) return;
        await _manager.DownloadUpdatesAsync(_pending, progress);
    }

    /// <summary>Closes the app, runs the installer's update step, and starts the new version.</summary>
    public void ApplyAndRestart()
    {
        if (_pending is null) return;
        _manager.ApplyUpdatesAndRestart(_pending);
    }
}
