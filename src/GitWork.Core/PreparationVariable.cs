namespace GitWork.Core;

public sealed class PreparationVariable
{
    public string Name { get; set; } = "";

    public string Value { get; set; } = "";

    public bool IsSecret { get; set; }
}