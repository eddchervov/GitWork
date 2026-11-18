namespace GitWork.Core;

public sealed record GitCommitInformation(
    string Hash,
    string Author,
    string AuthorEmail,
    string AuthoredAt,
    string Committer,
    string CommitterEmail,
    string CommittedAt,
    string Parents,
    string Message);
