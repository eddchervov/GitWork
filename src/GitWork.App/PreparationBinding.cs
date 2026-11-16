namespace GitWork.App;

public sealed class PreparationBinding
{
    public string Scope { get; set; } = "Repository";

    public string Path { get; set; } = "";

    public string ProfileId { get; set; } = "";

    public bool RunAutomatically { get; set; } = true;
}
