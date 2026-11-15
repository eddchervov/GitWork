namespace GitWork.Core;

public sealed class ProjectGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Новый проект";

    public List<string> RepositoryPaths { get; set; } = [];
}
