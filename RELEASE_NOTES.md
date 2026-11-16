# Git Work 0.3.1

- Save reusable CMD and PowerShell preparation profiles with ordered steps, environment variables and working directories.
- Select a profile when creating a worktree or bundle, including worktrees created from commits.
- Remember defaults per repository or parent-folder group. Skip preparation for one creation without changing the saved default.
- Start bundle preparation only after all worktrees have been created and saved; keep working copies if a command fails or is stopped.
- Show live step results and a selectable, exportable log. Retry failed preparation while preserving successful steps.
- Run preparation again from worktree actions, bundle settings or the File menu.
- Encrypt secret variables for the current Windows user, pass them through the environment and mask them in logs. Hide oversized output lines rather than exposing fragments of a secret.
- Keep step working directories within the new folder and preserve shell exit codes for error handling.
