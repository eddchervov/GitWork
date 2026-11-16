using System.ComponentModel;
using GitWork.Core;

namespace GitWork.App;

public sealed class RepositoryRowState(string path) : INotifyPropertyChanged
{
    private readonly string _path = path;

    public string Branch { get; private set; } = "";

    public int ChangeCount { get; private set; }

    public bool HasChanges => this.ChangeCount > 0 && !this.HasError;

    public bool HasError { get; private set; }

    public string Description { get; private set; } = path;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(WorktreeInfo? tree, int changeCount, string? error = null)
    {
        this.Branch = tree?.BranchLabel ?? "";
        this.ChangeCount = changeCount;
        this.HasError = error is not null;
        this.Description = this._path + (this.Branch.Length > 0 ? "\n" + this.Branch : "")
            + (error is not null ? "\n" + L10n.T(error)
                : changeCount > 0 ? "\n" + L10n.T("Незакоммиченных файлов: {0}", changeCount) : "");
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
