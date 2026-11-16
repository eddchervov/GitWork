namespace GitWork.Core;

public sealed record PreparationRunResult(
    IReadOnlyList<PreparationStepResult> Steps,
    int? FailedStepIndex,
    bool IsCanceled)
{
    public bool Success => !this.IsCanceled && this.FailedStepIndex is null
        && this.Steps.All(step => step.IsSkipped || step.ExitCode == 0);
}