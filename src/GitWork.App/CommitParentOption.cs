namespace GitWork.App;

public sealed record CommitParentOption(int Number, string Label)
{
    public override string ToString() => this.Label;
}
