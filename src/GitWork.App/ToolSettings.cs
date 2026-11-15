namespace GitWork.App;

public sealed class ToolSettings
{
    public string IdeKind { get; set; } = "Default";

    public string IdePath { get; set; } = "";

    public string TerminalKind { get; set; } = "PowerShell";

    public string TerminalPath { get; set; } = "";
}

public sealed record ToolChoice(string Kind, string Name, string Path)
{
    public override string ToString() => this.Name;
}
