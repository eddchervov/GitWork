using System.Windows;
using GitWork.Core;

namespace GitWork.App;

internal static class PreparationLauncher
{
    public static bool Show(Window owner, AppSettings settings, string repositoryPath, string rootPath, bool bundle = false,
        PreparationProfile? profile = null, bool choose = false)
    {
        if (choose)
        {
            var dialog = new PreparationLaunchWindow(settings, repositoryPath, rootPath, bundle) { Owner = owner };
            if (dialog.ShowDialog() != true)
            {
                return false;
            }

            profile = dialog.Profile;
        }

        if (profile is null)
        {
            return false;
        }

        PreparationPreferences.Remember(settings, repositoryPath, profile.Id, true, false, rootPath);
        new PreparationRunWindow(profile, rootPath) { Owner = owner }.ShowDialog();

        return true;
    }
}
