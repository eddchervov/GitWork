namespace GitWork.Core;

public sealed class PreparationProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public List<PreparationStep> Steps { get; set; } = [];

    public List<PreparationVariable> Variables { get; set; } = [];

    public override string ToString() => this.Name;
}
