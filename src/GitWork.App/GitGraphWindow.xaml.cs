using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitWork.Core;

namespace GitWork.App;

public partial class GitGraphWindow : Window
{
    private readonly GitHistoryView _history;

    public GitGraphWindow(IReadOnlyList<GraphRepositoryChoice> repositories, GitService git)
    {
        this.InitializeComponent();
        this._history = new GitHistoryView(repositories, git);
        ((Grid)this.Content).Children.Add(this._history);
        foreach (var name in new[] { "CommitsList", "CommitFiles", "CommitDiff", "FetchButton", "PullButton", "GraphModePicker" })
        {
            this.RegisterName(name, this._history.FindName(name));
        }

        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.Closing += (_, e) => e.Cancel = !this._history.CanLeave;
        this.Closed += (_, _) => this._history.Detach();
    }
}

public sealed record GraphRepositoryChoice(string Name, string Path)
{
    public override string ToString() => this.Name;
}

public sealed record GraphCommitItem(GitGraphRow Row, double GraphWidth, int LaneCount = 0, bool IsCompact = false, bool IsExpanded = false)
{
    public bool CanExpandMerge => this.IsCompact && this.Row.Commit.Parents.Count > 1;

    public string MergeToggleLabel => this.IsExpanded ? "−" : "+";

    public string MergeToggleHint => this.IsExpanded ? L10n.T("Свернуть историю, вошедшую этим слиянием")
        : L10n.T("Показать до {0} коммитов влитой истории. Другие ветки останутся свёрнутыми.", GitGraphService.MaximumSideCommitCount);

    public string GraphHint => this.Row.HasContinuation ? L10n.T("Продолжение находится в свёрнутой истории или за пределами загруженного участка")
        : this.CanExpandMerge && !this.IsExpanded ? L10n.T("Влитая история свёрнута. Нажмите «+» у слияния.") : "";

    public string DateLabel => DateTimeOffset.TryParse(this.Row.Commit.Date, out var date) ? date.ToLocalTime().ToString("dd.MM.yy HH:mm") : this.Row.Commit.Date;

    public string MetadataLabel => $"{this.Row.Commit.ShortHash} · {this.DateLabel} · {this.Row.Commit.Author}";

    public IReadOnlyList<GraphReferenceBadge> Badges
    {
        get
        {
            var references = this.Row.Commit.References.Distinct(StringComparer.Ordinal)
                .OrderBy(reference => reference == "HEAD" ? 0 : reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? 1
                    : reference.StartsWith("refs/remotes/", StringComparison.Ordinal) ? 2 : 3)
                .ThenBy(reference => reference, StringComparer.Ordinal).ToArray();
            var badges = references.Take(references.Length > 4 ? 3 : 4).Select(GraphReferenceBadge.Create).ToList();
            if (references.Length > 4)
            {
                badges.Add(new GraphReferenceBadge($"+{references.Length - 3}", string.Join("\n", references), Brushes.SlateGray, Brushes.White));
            }

            return badges;
        }
    }
}

public sealed record GraphModeOption(bool ShowAll, string Name)
{
    public override string ToString() => this.Name;
}

public sealed record GraphReferenceBadge(string Name, string FullName, Brush Background, Brush Foreground)
{
    public static GraphReferenceBadge Create(string reference)
    {
        var color = reference == "HEAD" ? "#23786D" : reference.StartsWith("refs/remotes/", StringComparison.Ordinal) ? "#3A6188"
            : reference.StartsWith("refs/tags/", StringComparison.Ordinal) ? "#805E9D" : "#376E5F";
        var name = reference.Replace("refs/heads/", "", StringComparison.Ordinal).Replace("refs/remotes/", "", StringComparison.Ordinal)
            .Replace("refs/tags/", "◇ ", StringComparison.Ordinal);
        var background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        background.Freeze();

        return new GraphReferenceBadge(name, reference, background, Brushes.White);
    }
}
