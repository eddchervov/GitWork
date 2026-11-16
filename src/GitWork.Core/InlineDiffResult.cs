namespace GitWork.Core;

public sealed record InlineDiffResult(IReadOnlyList<InlineDiffSpan> OldSpans, IReadOnlyList<InlineDiffSpan> NewSpans)
{
    public static InlineDiffResult Empty { get; } = new([], []);
}
