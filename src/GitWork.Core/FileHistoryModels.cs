namespace GitWork.Core;

public sealed record FileHistoryEntry(
    string FullHash,
    string ShortHash,
    string Subject,
    string Author,
    string Date,
    string RevisionPath,
    string? PreviousPath,
    string? FirstParentHash,
    bool IsAdded,
    bool IsDeleted)
{
    public string DisplayName => $"{this.ShortHash} · {this.Subject}";

    public string DisplayDate => DateTimeOffset.TryParse(this.Date, out var date) ? date.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : this.Date;
}

public sealed record FileContent(string Text, string? Message, bool IsBinary);
