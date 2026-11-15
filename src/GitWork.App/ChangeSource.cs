using GitWork.Core;

namespace GitWork.App;

public sealed record ChangeSource(string Name, string Path, IReadOnlyList<ChangedFile> Files);
