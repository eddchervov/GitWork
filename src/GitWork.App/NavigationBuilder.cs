using System.IO;
using GitWork.Core;

namespace GitWork.App;

public static class NavigationBuilder
{
    public static IReadOnlyList<NavigationNode> Build(
        IReadOnlyList<RepositoryInfo> repositories,
        IReadOnlyDictionary<string, IReadOnlyList<WorktreeInfo>> worktrees,
        IReadOnlyList<ProjectWorkspace> workspaces,
        string query,
        string mode,
        IReadOnlyList<string>? favoriteIds = null,
        IReadOnlyList<string>? recentIds = null,
        bool linkedOnly = false)
    {
        var roots = new List<NavigationNode>();
        var claimedPaths = workspaces.SelectMany(workspace => workspace.WorktreePaths.Values)
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (mode != "Loose")
        {
            var favorites = favoriteIds ?? [];
            var recent = recentIds ?? [];
            var recentOrder = recent.ToArray();
            var visibleWorkspaces = workspaces.Where(workspace => mode switch
            {
                "Favorites" => favorites.Contains(workspace.Id),
                "Recent" => recent.Contains(workspace.Id),
                _ => true
            });

            var bundles = visibleWorkspaces.OrderByDescending(workspace => mode != "Recent" && favorites.Contains(workspace.Id))
                .ThenBy(workspace => recent.Contains(workspace.Id) ? Array.IndexOf(recentOrder, workspace.Id) : int.MaxValue)
                .ThenBy(workspace => workspace.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(workspace => new NavigationNode
                {
                    Key = "workspace:" + workspace.Id,
                    Kind = "Workspace",
                    Title = (favorites.Contains(workspace.Id) ? "★ " : "") + workspace.DisplayName,
                    Subtitle = "",
                    Count = workspace.WorktreePaths.Count,
                    ToolTip = workspace.RootPath ?? "Общая папка не определена",
                    Workspace = workspace,
                    Children = workspace.WorktreePaths.Select(pair => NavigationBuilder.CreateWorktreeNode(
                        repositories.FirstOrDefault(repository => GitService.PathsEqual(repository.Path, pair.Key))
                            ?? new RepositoryInfo(Path.GetFileName(pair.Key), pair.Key, ""),
                        worktrees.FirstOrDefault(item => GitService.PathsEqual(item.Key, pair.Key)).Value
                            ?.FirstOrDefault(tree => GitService.PathsEqual(tree.Path, pair.Value))
                            ?? new WorktreeInfo(pair.Value, "", "", false, false, false, true, "Копия не найдена"),
                        workspace.Id)).ToArray()
                }).ToArray();

            var title = mode == "Favorites" ? "Избранные комплекты" : mode == "Recent" ? "Недавние комплекты" : "Комплекты";
            roots.Add(new NavigationNode { Key = "bundles", Kind = "Section", Title = title, Count = bundles.Length, IsExpanded = true, Children = bundles });
        }

        if (mode is not ("Bundles" or "Favorites" or "Recent"))
        {
            var loose = new List<NavigationNode>();

            foreach (var repository in repositories)
            {
                var copies = worktrees.GetValueOrDefault(repository.Path) ?? [];
                var children = copies.Where(tree => (!linkedOnly || !tree.IsMain && !tree.IsBare) && !claimedPaths.Contains(Path.GetFullPath(tree.Path)))
                    .Select(tree => NavigationBuilder.CreateWorktreeNode(repository, tree, null)).ToArray();

                if (children.Length == 1)
                {
                    var tree = children[0].Worktree!;
                    var sameNames = repositories.Where(candidate => candidate.Name.Equals(repository.Name, StringComparison.OrdinalIgnoreCase));
                    var parent = Path.GetDirectoryName(repository.Path) ?? repository.Path;
                    var location = sameNames.Count() > 1 ? Path.GetFileName(parent) : "";

                    if (location.Length > 0 && sameNames.Count(candidate => Path.GetFileName(Path.GetDirectoryName(candidate.Path)) == location) > 1)
                    {
                        location = parent;
                    }

                    loose.Add(new NavigationNode
                    {
                        Key = children[0].Key,
                        Kind = "Worktree",
                        Title = repository.Name,
                        Subtitle = tree.BranchLabel + (location.Length > 0 ? " · " + location : ""),
                        ToolTip = tree.Path,
                        Repository = repository,
                        Worktree = tree
                    });
                }
                else if (children.Length > 1)
                {
                    loose.Add(new NavigationNode
                    {
                        Key = "repository:" + repository.Path,
                        Kind = "Repository",
                        Title = repository.Name,
                        Subtitle = repository.Path,
                        Count = children.Length,
                        ToolTip = repository.Path,
                        Repository = repository,
                        Children = children
                    });
                }
            }

            roots.Add(new NavigationNode { Key = "loose", Kind = "Section", Title = "Отдельные worktree", Count = loose.Sum(node => node.Worktree is null ? node.Children.Count : 1), IsExpanded = true, Children = loose });
        }

        return roots.Select(root => NavigationBuilder.Filter(root, query)).OfType<NavigationNode>().ToArray();
    }

    public static IEnumerable<NavigationNode> Flatten(IEnumerable<NavigationNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;

            foreach (var child in NavigationBuilder.Flatten(node.Children))
            {
                yield return child;
            }
        }
    }

    private static NavigationNode CreateWorktreeNode(RepositoryInfo repository, WorktreeInfo tree, string? workspaceId) => new()
    {
        Key = "worktree:" + (workspaceId ?? "loose") + ":" + Path.GetFullPath(tree.Path),
        Kind = "Worktree",
        Title = workspaceId is null ? tree.BranchLabel : repository.Name,
        Subtitle = tree.IsPrunable ? "Копия недоступна" : workspaceId is null ? tree.Path : tree.BranchLabel,
        ToolTip = tree.Path,
        Repository = repository,
        Worktree = tree
    };

    private static NavigationNode? Filter(NavigationNode node, string query)
    {
        if (string.IsNullOrWhiteSpace(query) || SearchMatcher.Matches(query, node.Title, node.Subtitle, node.ToolTip, node.Repository?.Path))
        {
            return node;
        }

        var children = node.Children.Select(child => NavigationBuilder.Filter(child, query)).OfType<NavigationNode>().ToArray();

        return children.Length == 0 ? null : new NavigationNode
        {
            Key = node.Key,
            Kind = node.Kind,
            Title = node.Title,
            Subtitle = node.Subtitle,
            Count = node.Count,
            ToolTip = node.ToolTip,
            Repository = node.Repository,
            Worktree = node.Worktree,
            Workspace = node.Workspace,
            Children = children,
            IsExpanded = true
        };
    }
}
