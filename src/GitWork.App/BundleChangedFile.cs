using GitWork.Core;

namespace GitWork.App;

public sealed record BundleChangedFile(string RepositoryName, string WorktreePath, ChangedFile File)
{
    public string StatusLabel => this.File.Status;

    public string DisplayPath => this.File.DisplayPath;
}
