namespace GitWork.Core;

public sealed record GitGraphCommit(
    string Hash,
    string ShortHash,
    string Subject,
    string Author,
    string Date,
    IReadOnlyList<string> Parents,
    IReadOnlyList<string> References,
    bool IsHead,
    IReadOnlyList<string>? DisplayParents = null);

public sealed record GitGraphLine(int FromLane, double FromY, int ToLane, double ToY, int ColorIndex);

public sealed record GitGraphRow(
    GitGraphCommit Commit,
    int TopLaneCount,
    int NodeLane,
    int NodeColorIndex,
    int BottomLaneCount,
    IReadOnlyList<GitGraphLine> Lines,
    bool HasContinuation = false,
    bool HasMoreMainline = false);
