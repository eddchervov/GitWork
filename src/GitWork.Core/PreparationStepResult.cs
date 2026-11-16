namespace GitWork.Core;

public sealed record PreparationStepResult(
    int StepIndex,
    string Name,
    int? ExitCode,
    bool IsSkipped,
    string Output,
    bool IsCanceled = false);