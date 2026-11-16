# Git Work 0.2.0

- Keep the Repositories and Worktree views independent, with Changes as the default repository view.
- Group repositories by parent folder; remove a repository from the list from its context menu without deleting its folder.
- See branch and uncommitted change indicators in the repository sidebar.
- Find and switch branches in one searchable local and remote list with the current branch pinned. Local branches switch directly when the working tree is clean; remote branches open the create-branch dialog.
- Open the selected repository's IDE, terminal, or folder from the header. History sync controls share a row with the history tabs.
- Delete a bundle's entire workspace root, including leftover `.artifacts`, after all worktrees have been deregistered.
- Warn and ask for confirmation when tracked or untracked files have uncommitted changes; ignored files such as `bin` and `obj` do not block deletion.
- Protect main and bare repositories. If root cleanup fails, keep the workspace entry available for retry.
