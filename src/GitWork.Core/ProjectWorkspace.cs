namespace GitWork.Core;

public sealed class ProjectWorkspace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ProjectId { get; set; } = "";

    public string Name { get; set; } = "";

    public List<string> RepositoryPaths { get; set; } = [];

    public string? PrimaryRepositoryPath { get; set; }

    public Dictionary<string, string> WorktreePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(this.Name) || this.Name is "Новая задача" or "Новая задача · неполный"
        ? this.RootPath is { } root ? System.IO.Path.GetFileName(root) : "Комплект без названия"
        : this.Name;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? RootPath
    {
        get
        {
            var parents = this.WorktreePaths.Values
                .Select(path => System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)))
                .ToArray();

            if (parents.Length == 0 || parents[0] is null)
            {
                return null;
            }

            var root = parents[0]!;

            return parents.All(parent => parent is not null && GitService.PathsEqual(parent, root)) ? root : null;
        }
    }

    public override string ToString() => this.Name;
}
