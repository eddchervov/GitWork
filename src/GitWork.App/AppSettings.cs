using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitWork.Core;

namespace GitWork.App;

public sealed class AppSettings
{
    public string MainSection { get; set; } = "Repositories";

    public List<string>? GitRepositoryPaths { get; set; }

    public List<string> RootPaths { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RootPath { get; set; }

    public string? SelectedRepository { get; set; }

    public List<string> AdditionalRepositories { get; set; } = [];

    public List<ProjectGroup> Projects { get; set; } = [];

    [JsonConverter(typeof(JsonStringEnumConverter<RepositoryGrouping>))]
    public RepositoryGrouping Grouping { get; set; } = RepositoryGrouping.Projects;

    public bool HasConfiguredProjects { get; set; }

    public List<ProjectWorkspace> Workspaces { get; set; } = [];

    public string? SelectedWorkspaceId { get; set; }

    public double SidebarWidth { get; set; } = 310;

    public string? SelectedNavigationKey { get; set; }

    public string NavigationMode { get; set; } = "All";

    public string Theme { get; set; } = "Dark";

    public string Language { get; set; } = "ru";

    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    public string? LastSeenUpdateVersion { get; set; }

    public bool AutoRefresh { get; set; } = true;

    public ToolSettings Tools { get; set; } = new();

    public List<string> FavoriteWorkspaceIds { get; set; } = [];

    public List<string> RecentWorkspaceIds { get; set; } = [];

    internal static string? SettingsDirectoryOverride { get; set; }

    public static string FilePath => Path.Combine(AppSettings.SettingsDirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitWork"),
        "settings.json");

    public static AppSettings Load()
    {
        var legacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WorktreeDesk",
            "settings.json");
        var sourcePath = AppSettings.SettingsDirectoryOverride is not null || File.Exists(AppSettings.FilePath)
            ? AppSettings.FilePath : legacyPath;
        if (!File.Exists(sourcePath))
        {
            return new AppSettings();
        }

        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(sourcePath)) ?? new AppSettings();
        settings.AdditionalRepositories ??= [];
        settings.RootPaths ??= [];
        settings.Projects ??= [];
        settings.Workspaces ??= [];
        settings.Workspaces = settings.Workspaces.Where(workspace => workspace is not null).ToList();
        settings.FavoriteWorkspaceIds = (settings.FavoriteWorkspaceIds ?? []).Where(id => settings.Workspaces.Any(workspace => workspace.Id == id)).Distinct().ToList();
        settings.RecentWorkspaceIds = (settings.RecentWorkspaceIds ?? []).Where(id => settings.Workspaces.Any(workspace => workspace.Id == id)).Distinct().Take(8).ToList();
        settings.Theme = settings.Theme == "Light" ? "Light" : "Dark";
        settings.Language = settings.Language == "en" ? "en" : "ru";
        settings.Tools ??= new ToolSettings();
        settings.Tools.IdePath ??= "";
        settings.Tools.TerminalPath ??= "";

        if (settings.Tools.IdeKind is not ("Default" or "VSCode" or "Rider" or "VisualStudio" or "Custom"))
        {
            settings.Tools.IdeKind = "Default";
        }

        if (settings.Tools.TerminalKind is not ("PowerShell" or "WindowsTerminal" or "GitBash" or "Custom"))
        {
            settings.Tools.TerminalKind = "PowerShell";
            settings.Tools.TerminalPath = "";
        }

        if (settings.NavigationMode is not ("All" or "Bundles" or "Loose" or "Favorites" or "Recent"))
        {
            settings.NavigationMode = "All";
        }

        foreach (var workspace in settings.Workspaces)
        {
            workspace.WorktreePaths ??= [];
            workspace.RepositoryPaths ??= [];

            if (workspace.RepositoryPaths.Count == 0)
            {
                workspace.RepositoryPaths = settings.Projects.FirstOrDefault(project => project?.Id == workspace.ProjectId)?.RepositoryPaths?.ToList()
                    ?? workspace.WorktreePaths.Keys.ToList();
            }

            var legacyName = settings.Projects.FirstOrDefault(project => project?.Id == workspace.ProjectId)?.Name;
            workspace.PrimaryRepositoryPath ??= workspace.RepositoryPaths.FirstOrDefault(path => Path.GetFileName(path) == legacyName)
                ?? workspace.RepositoryPaths.FirstOrDefault();
        }
        settings.Projects = settings.Projects.Where(project => project is not null && !string.IsNullOrWhiteSpace(project.Name)).ToList();

        foreach (var project in settings.Projects)
        {
            project.RepositoryPaths ??= [];
            project.RepositoryPaths = project.RepositoryPaths.Where(Path.IsPathFullyQualified).ToList();
        }

        if (!string.IsNullOrWhiteSpace(settings.RootPath))
        {
            settings.RootPaths = [settings.RootPath];
            settings.RootPath = null;
        }

        settings.RootPaths = settings.RootPaths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppSettings.FilePath)!);
        var temporaryPath = AppSettings.FilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, AppSettings.FilePath, true);
    }
}
