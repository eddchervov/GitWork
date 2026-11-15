namespace GitWork.Core;

public sealed record RepositoryEntry(RepositoryInfo Repository, string Section, string? ProjectId)
{
    public string Name => this.Repository.Name;

    public string Path => this.Repository.Path;

    public string Group => this.Repository.Group;
}
