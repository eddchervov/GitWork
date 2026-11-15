using System.Windows;
using GitWork.Core;

namespace GitWork.App;

public partial class RemoveWorkspaceRepositoryWindow : Window
{
    public RemoveWorkspaceRepositoryWindow(string repositoryName, WorkspaceRepositoryRemovalPreview preview, bool removesLastRepository)
    {
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.Heading.Text = L10n.T("Убрать {0} из комплекта?", repositoryName);
        this.PathText.Text = preview.WorktreePath ?? preview.RepositoryPath;
        this.DeleteCopyOption.IsEnabled = preview.CanDeleteWorkingCopy;
        this.DeleteReason.Text = preview.CanDeleteWorkingCopy
            ? L10n.T("Папка будет удалена, ветка сохранится.")
            : L10n.T(preview.DeleteBlockedReason ?? "Рабочую копию нельзя удалить с диска.");

        if (!preview.IsWorkingCopyAvailable)
        {
            this.SafetyWarning.Text = L10n.T("Рабочая копия уже отсутствует. Изменится только состав комплекта.");
        }
        else
        {
            var warnings = new List<string>();

            if (preview.ChangedFileCount > 0)
            {
                warnings.Add(L10n.T("Изменения: {0} файлов. При удалении только из комплекта они останутся на диске.", preview.ChangedFileCount));
            }

            if (preview.IgnoredFileCount > 0)
            {
                warnings.Add(L10n.T("Игнорируемые файлы: {0}. Они могут содержать локальные данные.", preview.IgnoredFileCount));
            }

            this.SafetyWarning.Text = string.Join(" ", warnings);
        }

        if (removesLastRepository)
        {
            this.SafetyWarning.Text += (this.SafetyWarning.Text.Length == 0 ? "" : " ")
                + L10n.T("Это последний репозиторий: после удаления комплект исчезнет из списка.");
        }
    }

    public bool DeleteWorkingCopy => this.DeleteCopyOption.IsChecked == true;

    private void RemovalOption_Checked(object sender, RoutedEventArgs e)
    {
        if (this.ConfirmButton is null)
        {
            return;
        }

        this.ConfirmButton.Content = sender is System.Windows.Controls.RadioButton { Name: nameof(this.DeleteCopyOption) }
            ? L10n.T("Удалить с диска")
            : L10n.T("Убрать из комплекта");
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = true;
    }
}
