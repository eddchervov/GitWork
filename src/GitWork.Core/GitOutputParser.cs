namespace GitWork.Core;

public static class GitOutputParser
{
    public static IReadOnlyList<WorktreeInfo> ParseWorktrees(string output)
    {
        var result = new List<WorktreeInfo>();
        string? path = null;
        var head = "";
        var branch = "";
        var isBare = false;
        var isLocked = false;
        var isPrunable = false;
        string? reason = null;

        foreach (var field in output.Split('\0'))
        {
            if (field.Length == 0)
            {
                if (path is null)
                {
                    continue;
                }

                result.Add(new WorktreeInfo(path, head, branch, result.Count == 0, isBare, isLocked, isPrunable, reason));
                path = null;
                head = branch = "";
                isBare = isLocked = isPrunable = false;
                reason = null;
                continue;
            }

            if (field.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = field[9..];
            }
            else if (field.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = field[5..];
            }
            else if (field.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                branch = field[18..];
            }
            else if (field == "bare")
            {
                isBare = true;
            }
            else if (field == "locked" || field.StartsWith("locked ", StringComparison.Ordinal))
            {
                isLocked = true;
                reason = field.Length > 7 ? field[7..] : "Рабочая копия заблокирована.";
            }
            else if (field == "prunable" || field.StartsWith("prunable ", StringComparison.Ordinal))
            {
                isPrunable = true;
                reason = field.Length > 9 ? field[9..] : "Рабочая папка недоступна.";
            }
        }

        return result;
    }

    public static IReadOnlyList<ChangedFile> ParseStatus(string output)
    {
        var fields = output.Split('\0');
        var files = new List<ChangedFile>();

        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (field.Length < 4)
            {
                continue;
            }

            var status = field[..2];
            var path = field[3..];
            string? originalPath = null;

            if ((status.Contains('R') || status.Contains('C')) && i + 1 < fields.Length)
            {
                originalPath = fields[++i];
            }

            files.Add(new ChangedFile(status, path, originalPath));
        }

        return files;
    }
}
