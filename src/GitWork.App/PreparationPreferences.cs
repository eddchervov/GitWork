using System.IO;
using GitWork.Core;

namespace GitWork.App;

internal static class PreparationPreferences
{
    public static PreparationBinding? Find(AppSettings settings, string repositoryPath, string? worktreePath = null)
    {
        var bindings = settings.PreparationBindings.Where(binding => settings.PreparationProfiles.Any(profile => profile.Id == binding.ProfileId)).ToArray();

        return bindings.LastOrDefault(binding => binding.Scope == "Worktree" && worktreePath is not null && GitService.PathsEqual(binding.Path, worktreePath))
            ?? bindings.LastOrDefault(binding => binding.Scope == "Repository" && GitService.PathsEqual(binding.Path, repositoryPath))
            ?? bindings.LastOrDefault(binding => binding.Scope == "Group" && GitService.PathsEqual(binding.Path, Path.GetDirectoryName(repositoryPath)!));
    }

    public static void Remember(AppSettings settings, string repositoryPath, string profileId, bool enabled, bool group, string? worktreePath = null)
    {
        var scope = worktreePath is not null ? "Worktree" : group ? "Group" : "Repository";
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath ?? (group ? Path.GetDirectoryName(repositoryPath)! : repositoryPath)));
        settings.PreparationBindings.RemoveAll(binding => binding.Scope == scope && GitService.PathsEqual(binding.Path, path));
        if (scope == "Group")
        {
            settings.PreparationBindings.RemoveAll(binding => binding.Scope == "Repository" && GitService.PathsEqual(binding.Path, repositoryPath));
        }

        settings.PreparationBindings.Add(new PreparationBinding { Scope = scope, Path = path, ProfileId = profileId, RunAutomatically = enabled });
        settings.Save();
    }

    public static IReadOnlyDictionary<string, string> GetEnvironment(PreparationProfile profile)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in profile.Variables)
        {
            environment.Add(variable.Name, variable.IsSecret ? PreparationSecrets.Unprotect(variable.Value) : variable.Value);
        }

        return environment;
    }
}
