namespace GitWork.Core;

public sealed class PreparationStep
{
    public string Name { get; set; } = "";

    public string Shell { get; set; } = "CMD";

    public string Command { get; set; } = "";

    public string WorkingDirectory { get; set; } = ".";

    public bool IsEnabled { get; set; } = true;

    public bool StopOnError { get; set; } = true;
}