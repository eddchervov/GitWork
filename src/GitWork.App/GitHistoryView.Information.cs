using System.Text;
using System.Windows;
using GitWork.Core;

namespace GitWork.App;

public partial class GitHistoryView
{
    private async void CommitInformation_Click(object sender, RoutedEventArgs e)
    {
        this.SelectContextCommit(sender);
        await this.ReadCommitInformationAsync(false);
    }

    private async void CopyCommitMessage_Click(object sender, RoutedEventArgs e)
    {
        this.SelectContextCommit(sender);
        await this.ReadCommitInformationAsync(true);
    }

    private async Task ReadCommitInformationAsync(bool copyMessage)
    {
        if (this._isBusy || this.SelectedRepository is not { } repository
            || this.CommitsList.SelectedItem is not GraphCommitItem item)
        {
            return;
        }

        this.SetBusy(true);
        this.OperationStatus.Text = L10n.T("Чтение сведений о коммите…");
        try
        {
            var information = await this._details.GetInformationAsync(repository.Path, item.Row.Commit.Hash);
            if (this._isClosed)
            {
                return;
            }

            this.OperationStatus.Text = "";
            if (copyMessage)
            {
                if (information.Message.Length > 0)
                {
                    Clipboard.SetText(information.Message);
                }

                return;
            }

            var text = new StringBuilder();
            text.AppendLine($"{L10n.T("Репозиторий")}: {repository.Path}");
            text.AppendLine($"{L10n.T("Хеш")}: {information.Hash}");
            text.AppendLine($"{L10n.T("Автор")}: {information.Author} <{information.AuthorEmail}>");
            text.AppendLine($"{L10n.T("Дата автора")}: {information.AuthoredAt}");
            text.AppendLine($"{L10n.T("Создал коммит")}: {information.Committer} <{information.CommitterEmail}>");
            text.AppendLine($"{L10n.T("Дата коммита")}: {information.CommittedAt}");
            text.AppendLine($"{L10n.T("Родительские коммиты")}: {information.Parents}");
            if (item.Row.Commit.References.Count > 0)
            {
                text.AppendLine($"{L10n.T("Ветки и теги")}: {string.Join(", ", item.Row.Commit.References)}");
            }

            text.AppendLine();
            text.Append(information.Message);
            new InformationWindow(L10n.T("Сведения о коммите"), text.ToString()) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            this.OperationStatus.Text = L10n.T("Буфер обмена занят. Повторите копирование.");
        }
        catch (Exception exception) when (GitHistoryView.IsExpected(exception))
        {
            this.OperationStatus.Text = L10n.T(exception.Message);
        }
        finally
        {
            this.SetBusy(false);
        }
    }
}
