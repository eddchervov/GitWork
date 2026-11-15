using Velopack;

namespace GitWork.App;

public enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    NoReleaseSource,
    NotPublished,
    Unavailable
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    string? LatestVersion = null,
    string? ReleaseNotes = null,
    Uri? InstallerUrl = null,
    UpdateInfo? UpdateInfo = null,
    string? ErrorMessage = null,
    bool IsInstalled = false)
{
    public bool HasUpdate => this.Status == UpdateCheckStatus.UpdateAvailable;
}
