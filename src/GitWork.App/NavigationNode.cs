using System.ComponentModel;
using System.Runtime.CompilerServices;
using GitWork.Core;

namespace GitWork.App;

public sealed class NavigationNode : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isExpanded;
    private NavigationStatus? _status;

    public required string Key { get; init; }

    public required string Kind { get; init; }

    public required string Title { get; init; }

    public string Subtitle { get; init; } = "";

    public int? Count { get; init; }

    public string CountLabel => this.Count is { } count ? $"({count})" : "";

    public string ToolTip { get; init; } = "";

    public RepositoryInfo? Repository { get; init; }

    public WorktreeInfo? Worktree { get; init; }

    public ProjectWorkspace? Workspace { get; init; }

    public IReadOnlyList<NavigationNode> Children { get; init; } = [];

    public NavigationStatus? Status
    {
        get => this._status;
        set
        {
            if (this._status == value)
            {
                return;
            }

            this._status = value;
            this.NotifyChanged();
        }
    }

    public bool IsSelected
    {
        get => this._isSelected;
        set
        {
            if (this._isSelected == value)
            {
                return;
            }

            this._isSelected = value;
            this.NotifyChanged();
        }
    }

    public bool IsExpanded
    {
        get => this._isExpanded;
        set
        {
            if (this._isExpanded == value)
            {
                return;
            }

            this._isExpanded = value;
            this.NotifyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyChanged([CallerMemberName] string? propertyName = null) =>
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
