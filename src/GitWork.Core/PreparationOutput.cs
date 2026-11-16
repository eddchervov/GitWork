namespace GitWork.Core;

public sealed record PreparationOutput(
    int StepIndex,
    string Text,
    bool IsError = false,
    bool IsStarting = false,
    PreparationStepResult? Result = null);
