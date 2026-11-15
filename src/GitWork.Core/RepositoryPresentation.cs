namespace GitWork.Core;

public static class RepositoryPresentation
{
    public static IReadOnlyList<RepositoryEntry> GetEntries(
        IReadOnlyList<RepositoryInfo> repositories,
        IReadOnlyList<ProjectGroup> projects,
        RepositoryGrouping grouping,
        string query)
    {
        var entries = new List<RepositoryEntry>();

        foreach (var repository in repositories)
        {
            var memberships = projects.Where(project => project.RepositoryPaths.Any(path => GitService.PathsEqual(path, repository.Path))).ToArray();
            var projectNames = string.Join(" ", memberships.Select(project => project.Name));

            if (grouping == RepositoryGrouping.Projects)
            {
                if (memberships.Length == 0 && SearchMatcher.Matches(query, repository.Name, repository.Path, repository.Group))
                {
                    entries.Add(new RepositoryEntry(repository, "Без проекта", null));
                }

                foreach (var project in memberships)
                {
                    if (SearchMatcher.Matches(query, repository.Name, repository.Path, repository.Group, project.Name))
                    {
                        entries.Add(new RepositoryEntry(repository, project.Name, project.Id));
                    }
                }

                continue;
            }

            if (!SearchMatcher.Matches(query, repository.Name, repository.Path, repository.Group, projectNames))
            {
                continue;
            }

            var section = grouping == RepositoryGrouping.Folders ? System.IO.Path.GetDirectoryName(repository.Path) ?? repository.Group : "";
            entries.Add(new RepositoryEntry(repository, section, null));
        }

        return entries.OrderBy(entry => entry.Section == "Без проекта")
            .ThenBy(entry => entry.Section, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
