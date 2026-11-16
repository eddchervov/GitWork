using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitWork.Core;

namespace GitWork.App;

public enum CommitOperationKind
{
    Branch,
    Worktree,
    CherryPick
}

public partial class CommitOperationWindow : Window
{
    private readonly CommitOperationKind _kind;
    private readonly string _repository;
    private readonly GitService _git;
    private readonly GitCommitOperationService _operations;
    private readonly GitCommitOperationPreview _preview;
    private readonly IReadOnlyList<WorktreeInfo> _worktrees;
    private readonly string? _startingBranch;
    private readonly string _startingHead;
    private readonly AppSettings _settings;
    private readonly string _preparationRepository;
    private bool _isBusy;

    public CommitOperationWindow(
        CommitOperationKind kind,
        string repository,
        GitService git,
        GitCommitOperationService operations,
        GitCommitOperationPreview preview,
        IReadOnlyList<BranchInfo> branches,
        IReadOnlyList<WorktreeInfo> worktrees,
        string? startingBranch,
        string startingHead,
        AppSettings? settings = null)
    {
        this.InitializeComponent();
        this._kind = kind;
        this._repository = repository;
        this._git = git;
        this._operations = operations;
        this._preview = preview;
        this._worktrees = worktrees;
        this._startingBranch = startingBranch;
        this._startingHead = startingHead;
        this._settings = settings ?? AppSettings.Load();
        this._preparationRepository = worktrees.FirstOrDefault(tree => tree.IsMain)?.Path ?? repository;
        this.Preparation.Configure(this._settings, this._preparationRepository);
        this.TitleText.Text = kind switch
        {
            CommitOperationKind.Branch => L10n.T("Создать ветку от коммита"),
            CommitOperationKind.Worktree => L10n.T("Создать worktree от коммита"),
            _ => L10n.T("Перенести коммит в ветку")
        };
        this.SourceText.Text = $"{preview.CommitHash[..Math.Min(10, preview.CommitHash.Length)]} · {preview.Subject}";
        this.BranchFields.Visibility = kind == CommitOperationKind.Branch ? Visibility.Visible : Visibility.Collapsed;
        this.WorktreeFields.Visibility = kind == CommitOperationKind.Worktree ? Visibility.Visible : Visibility.Collapsed;
        this.CherryFields.Visibility = kind == CommitOperationKind.CherryPick ? Visibility.Visible : Visibility.Collapsed;
        this.MergeParentFields.Visibility = kind == CommitOperationKind.CherryPick && preview.IsMerge ? Visibility.Visible : Visibility.Collapsed;
        this.MergeParent.ItemsSource = preview.ParentHashes.Select((hash, index) => new CommitParentOption(index + 1,
            L10n.T("Родитель {0}: {1}", index + 1, hash[..Math.Min(10, hash.Length)]))).ToArray();
        this.WorktreePath.Text = Path.Combine(Path.GetDirectoryName(repository)!, $"{Path.GetFileName(repository)}-{preview.CommitHash[..7]}");
        var targets = branches.Where(branch => !branch.IsRemote).Select(branch => new CherryPickBranchChoice(branch, repository)).ToArray();
        this.TargetBranch.ItemsSource = targets;
        this.TargetBranch.SelectedItem = targets.FirstOrDefault(target => target.Branch.Name == startingBranch);
        this.Closing += this.Window_Closing;
        this.UpdateTargetBranch();
        this.UpdateRunButton();
    }

    public GitCommitOperationResult? Result { get; private set; }

    public PreparationProfile? PreparationProfile => this.Preparation.ProfileToRun;

    public string? CreatedWorktreePath => this._kind == CommitOperationKind.Worktree && this.Result is not null
        ? Path.GetFullPath(this.WorktreePath.Text.Trim()) : null;

    public string PreparationRepository => this._preparationRepository;

    private void TargetBranch_Changed(object sender, SelectionChangedEventArgs e) => this.UpdateTargetBranch();

    private void MergeParent_Changed(object sender, SelectionChangedEventArgs e) => this.UpdateRunButton();

    private void UpdateTargetBranch()
    {
        if (this.TargetBranchNote is null)
        {
            return;
        }

        if (this.TargetBranch.SelectedItem is not CherryPickBranchChoice choice)
        {
            this.TargetBranchNote.Text = L10n.T("Выберите локальную ветку.");
        }
        else if (this.IsOccupiedByAnotherWorktree(choice.Branch))
        {
            this.TargetBranchNote.Text = L10n.T("Ветка уже открыта в другой рабочей копии: {0}", choice.Branch.OccupiedPath);
        }
        else if (choice.Branch.Name != this._startingBranch)
        {
            this.TargetBranchNote.Text = L10n.T("Текущая рабочая копия переключится с «{0}» на «{1}». Незакоммиченных изменений быть не должно.", this._startingBranch ?? L10n.T("HEAD без ветки"), choice.Branch.Name);
        }
        else
        {
            this.TargetBranchNote.Text = L10n.T("Изменения коммита будут применены в «{0}» текущей рабочей копии.", choice.Branch.Name);
        }

        this.UpdateRunButton();
    }

    private void UpdateRunButton()
    {
        if (this.RunButton is null)
        {
            return;
        }

        this.RunButton.Content = this._kind switch
        {
            CommitOperationKind.Branch => L10n.T("Создать ветку"),
            CommitOperationKind.Worktree => L10n.T("Создать worktree"),
            _ => L10n.T("Применить cherry-pick")
        };
        this.RunButton.IsEnabled = !this._isBusy && (this._kind != CommitOperationKind.CherryPick
            || this.TargetBranch.SelectedItem is CherryPickBranchChoice choice && choice.IsAvailable && !this.IsOccupiedByAnotherWorktree(choice.Branch)
                && (!this._preview.IsMerge || this.MergeParent.SelectedItem is CommitParentOption));
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (this._isBusy)
        {
            return;
        }

        this.SetBusy(true);
        this.ErrorText.Text = "";

        try
        {
            var current = await this._operations.PreviewAsync(this._repository, this._preview.CommitHash);
            var head = (await this._git.RunAsync(this._repository, "rev-parse", "--verify", "HEAD")).TrimEnd('\r', '\n');
            if (current.CurrentBranch != this._startingBranch || head != this._startingHead)
            {
                throw new GitException("Текущая ветка или HEAD изменились. Закройте окно и откройте действие заново.");
            }

            if (this._kind == CommitOperationKind.Branch)
            {
                if (!current.CanCreateBranch)
                {
                    throw new GitException(current.BlockReason ?? "Создание ветки сейчас недоступно.");
                }

                this.Result = await this._operations.CreateBranchAsync(this._repository, current.CommitHash,
                    this.BranchName.Text.Trim(), this.CheckoutBranch.IsChecked == true);
            }
            else if (this._kind == CommitOperationKind.Worktree)
            {
                if (!current.CanCreateBranch)
                {
                    throw new GitException(current.BlockReason ?? "Создание worktree сейчас недоступно.");
                }

                if (!this.Preparation.RememberSelection())
                {
                    return;
                }

                this.Result = await this._operations.CreateWorktreeFromCommitAsync(this._repository, current.CommitHash,
                    this.WorktreeBranchName.Text.Trim(), this.WorktreePath.Text.Trim());
                this.Preparation.RememberSelection(this.CreatedWorktreePath);
            }
            else
            {
                await this.CherryPickAsync(current);
            }

            this.SetBusy(false);
            this.DialogResult = true;
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            this.ErrorText.Text = L10n.T(exception.Message);
        }
        finally
        {
            this.SetBusy(false);
        }
    }

    private async Task CherryPickAsync(GitCommitOperationPreview current)
    {
        if (!current.CanCreateBranch)
        {
            throw new GitException(current.BlockReason ?? "Cherry-pick сейчас недоступен.");
        }

        if ((await this._git.GetStatusAsync(this._repository, true)).Count > 0)
        {
            throw new GitException("Сначала сохраните или отмените изменения в текущей рабочей копии.");
        }

        var target = (this.TargetBranch.SelectedItem as CherryPickBranchChoice)?.Branch
            ?? throw new GitException("Выберите целевую локальную ветку.");
        var worktrees = await this._git.GetWorktreesAsync(this._repository);
        var branches = await this._git.GetBranchesAsync(this._repository, worktrees);
        var freshTarget = branches.FirstOrDefault(branch => !branch.IsRemote && branch.Name == target.Name)
            ?? throw new GitException("Целевая ветка больше не существует. Откройте действие заново.");
        if (this.IsOccupiedByAnotherWorktree(freshTarget, worktrees))
        {
            throw new GitException($"Ветка «{target.Name}» уже используется другой рабочей копией: {freshTarget.OccupiedPath}");
        }

        if (this._preview.IsMerge && this.MergeParent.SelectedItem is not CommitParentOption)
        {
            throw new GitException("Для merge-коммита явно выберите основного родителя.");
        }

        if (target.Name != current.CurrentBranch)
        {
            await this._git.SwitchBranchAsync(this._repository, target.Name);
        }

        var switched = await this._operations.PreviewAsync(this._repository, current.CommitHash);
        if (switched.CurrentBranch != target.Name || !switched.CanCherryPick)
        {
            throw new GitException(switched.BlockReason ?? "Не удалось безопасно переключиться на выбранную ветку.");
        }

        this.Result = await this._operations.CherryPickAsync(this._repository, current.CommitHash,
            (this.MergeParent.SelectedItem as CommitParentOption)?.Number, target.Name);
    }

    private bool IsOccupiedByAnotherWorktree(BranchInfo branch) => this.IsOccupiedByAnotherWorktree(branch, this._worktrees);

    private bool IsOccupiedByAnotherWorktree(BranchInfo branch, IReadOnlyList<WorktreeInfo> worktrees)
    {
        var currentPath = Path.GetFullPath(this._repository);
        return worktrees.Any(worktree => worktree.Branch == branch.Name
            && !string.Equals(Path.GetFullPath(worktree.Path), currentPath, StringComparison.OrdinalIgnoreCase));
    }

    private void SetBusy(bool busy)
    {
        this._isBusy = busy;
        this.BranchFields.IsEnabled = !busy;
        this.WorktreeFields.IsEnabled = !busy;
        this.CherryFields.IsEnabled = !busy;
        this.CancelButton.IsEnabled = !busy;
        this.UpdateRunButton();
        this.Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!this._isBusy)
        {
            this.DialogResult = false;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (this._isBusy)
        {
            e.Cancel = true;
        }
    }
}
