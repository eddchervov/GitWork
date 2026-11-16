using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using GitWork.Core;

namespace GitWork.App;

public partial class PreparationProfilesWindow : Window
{
    private readonly List<PreparationProfile> _profiles;
    private bool _updating;
    private PreparationProfile? _editingProfile;
    private PreparationStep? _editingStep;

    public PreparationProfilesWindow(IReadOnlyList<PreparationProfile> profiles, string? selectedId = null)
    {
        this._profiles = profiles.Select(PreparationProfilesWindow.Clone).ToList();
        foreach (var variable in this._profiles.SelectMany(profile => profile.Variables).Where(variable => variable.IsSecret))
        {
            if (!variable.Value.StartsWith("dpapi:", StringComparison.Ordinal))
            {
                variable.Value = PreparationSecrets.Protect(variable.Value);
            }
        }

        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.ProfilePicker.ItemsSource = this._profiles;
        this.ProfilePicker.SelectedItem = this._profiles.FirstOrDefault(profile => profile.Id == selectedId)
            ?? this._profiles.FirstOrDefault();
        if (this.ProfilePicker.SelectedItem is null)
        {
            this.SetEditors(null);
        }
    }

    public IReadOnlyList<PreparationProfile> Profiles => this._profiles.Select(PreparationProfilesWindow.Clone).ToArray();

    public string? SelectedProfileId => (this.ProfilePicker.SelectedItem as PreparationProfile)?.Id;

    private static PreparationProfile Clone(PreparationProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        Steps = profile.Steps.Select(step => new PreparationStep
        {
            Name = step.Name,
            Shell = step.Shell,
            Command = step.Command,
            WorkingDirectory = step.WorkingDirectory,
            IsEnabled = step.IsEnabled,
            StopOnError = step.StopOnError
        }).ToList(),
        Variables = profile.Variables.Select(variable => new PreparationVariable
        {
            Name = variable.Name,
            Value = variable.Value,
            IsSecret = variable.IsSecret
        }).ToList()
    };

    private void SetEditors(PreparationProfile? profile)
    {
        this._updating = true;
        this._editingProfile = profile;
        this.ProfileNameBox.Text = profile?.Name ?? "";
        this.StepsList.ItemsSource = profile?.Steps;
        this.StepsList.SelectedIndex = profile?.Steps.Count > 0 ? 0 : -1;
        this.VariablesList.ItemsSource = profile?.Variables;
        this.DeleteProfileButton.IsEnabled = profile is not null;
        this._updating = false;
        this.SetStepEditor(profile?.Steps.FirstOrDefault());
    }

    private void SetStepEditor(PreparationStep? step)
    {
        this._updating = true;
        this._editingStep = step;
        this.StepNameBox.Text = step?.Name ?? "";
        this.CommandBox.Text = step?.Command ?? "";
        this.WorkingDirectoryBox.Text = step?.WorkingDirectory ?? ".";
        this.StopOnErrorBox.IsChecked = step?.StopOnError ?? true;
        this.ShellPicker.SelectedValue = step?.Shell;
        this.StepNameBox.IsEnabled = step is not null;
        this.CommandBox.IsEnabled = step is not null;
        this.WorkingDirectoryBox.IsEnabled = step is not null;
        this.ShellPicker.IsEnabled = step is not null;
        this.StopOnErrorBox.IsEnabled = step is not null;
        this.VariablesList.IsEnabled = step is not null;
        this._updating = false;
    }

    private void CommitProfileEditor()
    {
        if (this._editingProfile is { } profile)
        {
            profile.Name = this.ProfileNameBox.Text.Trim();
        }
    }

    private void CommitStepEditor()
    {
        if (this._editingStep is not { } step)
        {
            return;
        }

        step.Name = this.StepNameBox.Text.Trim();
        step.Command = this.CommandBox.Text;
        step.WorkingDirectory = this.WorkingDirectoryBox.Text.Trim();
        step.Shell = (this.ShellPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "CMD";
        step.StopOnError = this.StopOnErrorBox.IsChecked == true;
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (this._updating)
        {
            return;
        }

        this.CommitProfileEditor();
        this.CommitStepEditor();
        this.SetEditors(this.ProfilePicker.SelectedItem as PreparationProfile);
        this.ValidationText.Text = "";
    }

    private void ProfileName_Changed(object sender, TextChangedEventArgs e)
    {
        if (this._updating)
        {
            return;
        }

        this.CommitProfileEditor();
        var selected = this.ProfilePicker.SelectedItem;
        this.ProfilePicker.Items.Refresh();
        this.ProfilePicker.SelectedItem = selected;
    }

    private void Step_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (this._updating)
        {
            return;
        }

        this.CommitStepEditor();
        this.SetStepEditor(this.StepsList.SelectedItem as PreparationStep);
    }

    private void StepEditor_Changed(object sender, RoutedEventArgs e)
    {
        if (!this._updating)
        {
            this.CommitStepEditor();
            this.StepsList.Items.Refresh();
        }
    }

    private void StepEditor_Changed(object sender, TextChangedEventArgs e)
    {
        if (!this._updating)
        {
            this.CommitStepEditor();
            this.StepsList.Items.Refresh();
        }
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        this.CommitProfileEditor();
        this.CommitStepEditor();
        var profile = new PreparationProfile
        {
            Name = L10n.T("Новый профиль"),
            Steps = [new PreparationStep { Name = L10n.T("Новый шаг"), Command = "" }]
        };
        this._profiles.Add(profile);
        this.ProfilePicker.Items.Refresh();
        this.ProfilePicker.SelectedItem = profile;
        this.ProfileNameBox.Focus();
        this.ProfileNameBox.SelectAll();
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (this.ProfilePicker.SelectedItem is not PreparationProfile profile)
        {
            return;
        }

        var index = this.ProfilePicker.SelectedIndex;
        this._profiles.Remove(profile);
        this.ProfilePicker.Items.Refresh();
        this.ProfilePicker.SelectedItem = this._profiles.ElementAtOrDefault(Math.Min(index, this._profiles.Count - 1));
        if (this.ProfilePicker.SelectedItem is null)
        {
            this.SetEditors(null);
        }
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        if (this.ProfilePicker.SelectedItem is not PreparationProfile profile)
        {
            return;
        }

        this.CommitStepEditor();
        var step = new PreparationStep { Name = L10n.T("Новый шаг"), Command = "" };
        profile.Steps.Add(step);
        this.StepsList.Items.Refresh();
        this.StepsList.SelectedItem = step;
        this.StepNameBox.Focus();
        this.StepNameBox.SelectAll();
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        if (this.ProfilePicker.SelectedItem is not PreparationProfile profile || this.StepsList.SelectedItem is not PreparationStep step)
        {
            return;
        }

        var index = this.StepsList.SelectedIndex;
        profile.Steps.Remove(step);
        this.StepsList.Items.Refresh();
        this.StepsList.SelectedItem = profile.Steps.ElementAtOrDefault(Math.Min(index, profile.Steps.Count - 1));
        this.SetStepEditor(this.StepsList.SelectedItem as PreparationStep);
    }

    private void MoveStepUp_Click(object sender, RoutedEventArgs e) => this.MoveStep(-1);

    private void MoveStepDown_Click(object sender, RoutedEventArgs e) => this.MoveStep(1);

    private void MoveStep(int offset)
    {
        if (this.ProfilePicker.SelectedItem is not PreparationProfile profile || this.StepsList.SelectedItem is not PreparationStep step)
        {
            return;
        }

        this.CommitStepEditor();
        var index = profile.Steps.IndexOf(step);
        var newIndex = index + offset;
        if (newIndex < 0 || newIndex >= profile.Steps.Count)
        {
            return;
        }

        profile.Steps.RemoveAt(index);
        profile.Steps.Insert(newIndex, step);
        this.StepsList.Items.Refresh();
        this.StepsList.SelectedItem = step;
    }

    private void AddVariable_Click(object sender, RoutedEventArgs e) => this.EditVariable(null);

    private void EditVariable_Click(object sender, RoutedEventArgs e) => this.EditVariable(this.VariablesList.SelectedItem as PreparationVariable);

    private void EditVariable(PreparationVariable? variable)
    {
        if (this.ProfilePicker.SelectedItem is not PreparationProfile profile)
        {
            return;
        }

        this.CommitStepEditor();
        PreparationVariable? initial;
        try
        {
            initial = variable is null ? null : new PreparationVariable
            {
                Name = variable.Name,
                Value = variable.IsSecret ? PreparationSecrets.Unprotect(variable.Value) : variable.Value,
                IsSecret = variable.IsSecret
            };
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or FormatException)
        {
            this.ValidationText.Text = L10n.T("Не удалось прочитать секрет «{0}».", variable!.Name);

            return;
        }
        var dialog = new PreparationVariableWindow(initial) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } edited)
        {
            return;
        }

        if (variable is null)
        {
            profile.Variables.Add(edited);
        }
        else
        {
            variable.Name = edited.Name;
            variable.Value = edited.Value;
            variable.IsSecret = edited.IsSecret;
        }

        this.VariablesList.Items.Refresh();
        this.VariablesList.SelectedItem = variable ?? edited;
    }

    private void DeleteVariable_Click(object sender, RoutedEventArgs e)
    {
        if (this.ProfilePicker.SelectedItem is PreparationProfile profile && this.VariablesList.SelectedItem is PreparationVariable variable)
        {
            profile.Variables.Remove(variable);
            this.VariablesList.Items.Refresh();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        this.CommitProfileEditor();
        this.CommitStepEditor();
        this.ValidationText.Text = this.ValidateProfiles();
        if (this.ValidationText.Text.Length > 0)
        {
            return;
        }

        this.DialogResult = true;
    }

    private string ValidateProfiles()
    {
        if (this._profiles.Count == 0)
        {
            return "";
        }

        if (this._profiles.Any(profile => string.IsNullOrWhiteSpace(profile.Name))
            || this._profiles.Select(profile => profile.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != this._profiles.Count)
        {
            return L10n.T("Названия профилей должны быть заполнены и не повторяться.");
        }

        foreach (var profile in this._profiles)
        {
            if (profile.Steps.Count == 0)
            {
                return L10n.T("В профиле «{0}» добавьте хотя бы один шаг.", profile.Name);
            }

            if (profile.Steps.Any(step => string.IsNullOrWhiteSpace(step.Name) || string.IsNullOrWhiteSpace(step.Command)))
            {
                return L10n.T("Заполните название и команду каждого шага профиля «{0}».", profile.Name);
            }

            if (profile.Steps.Any(step => step.Shell is not ("CMD" or "PowerShell")
                || !PreparationProfilesWindow.IsSafeWorkingDirectory(step.WorkingDirectory)))
            {
                return L10n.T("Проверьте оболочку и рабочую папку каждого шага профиля «{0}».", profile.Name);
            }

            if (profile.Variables.Any(variable => !Regex.IsMatch(variable.Name, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)
                || variable.Value is null)
                || profile.Variables.Select(variable => variable.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.Variables.Count)
            {
                return L10n.T("Переменные профиля «{0}» должны иметь уникальное имя и значение.", profile.Name);
            }
        }

        return "";
    }

    private static bool IsSafeWorkingDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GitWork-PreparationRoot"));
            var resolved = Path.GetFullPath(Path.Combine(root, value));

            return GitService.PathsEqual(root, resolved) || resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
