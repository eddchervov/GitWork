<div align="center">
<img src="src/GitWork.App/Assets/GitWork.png" width="80" alt="Git Work">

# Git Work

A focused Git client. A home for your worktrees.

**English** · [Русский](README.ru.md)

![Version](https://img.shields.io/badge/version-0.1_preview-70cbbb) ![Windows](https://img.shields.io/badge/Windows-x64-58687d) [![License: MIT](https://img.shields.io/badge/license-MIT-70cbbb)](LICENSE)
</div>

Git Work brings everyday Git and multi-repository workspaces into one Windows app. Review changes, follow history, and keep parallel tasks in separate worktrees without losing track of their folders or branches.

**0.1 is an early preview.** The workflow is still evolving; feedback and reproducible bug reports are welcome.

![Review changes in the dark theme](docs/images/changes-dark.png)

## Two spaces, one workflow

| Repositories | Worktree |
| --- | --- |
| Your manually added local repositories and clones. | Automatically discovered worktrees and multi-repository bundles. |
| Changes, commits, history, branch comparison and synchronization. | Create isolated copies, choose their branches and open each repository in your IDE. |
| Add a folder or clone a repository from its URL. | Search by name, branch or path; keep related copies together. |

Worktree discovery never automatically fills your Repositories list. Add a particular worktree there yourself when you want to use it independently.

## Review and commit

- Unified or side-by-side diffs, with added, changed and deleted files clearly distinguished.
- File checkboxes, a commit title and an optional description right below the changes list.
- Commit only the selected files; unchecked files stay outside that commit.
- A compact commit graph, file history, branches and worktrees from a commit, and cherry-pick.
- Fetch, pull and push with visible incoming and outgoing commit counts.
- Branch comparison with an explicit source and destination.

![Explore the commit history](docs/images/history-dark.png)

## Work on several repositories together

A bundle groups worktrees for one task. Each repository keeps its own branch and Git history. Open Git or Actions on the repository you need; launch its IDE, switch branches or create a branch there.

Removing a repository from a bundle can leave its folder intact. Physical worktree removal is a separate action with safety checks for uncommitted work.

## Install

1. Install [Git for Windows](https://git-scm.com/downloads/win), available on PATH.
2. Download the Windows x64 Setup from [GitHub Releases](https://github.com/eddchervov/GitWork/releases) once a release is published.
3. Run Setup and add your first repository.

The installer includes the .NET runtime. Until the first release is published, build from source below. Release packages are currently unsigned.

## Make it yours

Choose **File → Language → English / Русский** and switch between light and dark themes. The interface language does not translate your code, filenames, branch names or commit messages.

Use **Help → Check for updates** or the bell in the header. Installed builds can download and apply a published update after you choose to install it. A portable development build first needs Setup to enter the installed update flow. No separate update server is required: releases and the update feed live on GitHub.

## Build from source

Requires Windows, the .NET 10 SDK and Git for Windows.

~~~powershell
dotnet run --project src/GitWork.App/GitWork.App.csproj
~~~

Run the checks from the repository root:

~~~powershell
dotnet run --project tests/GitWork.Checks/GitWork.Checks.csproj
dotnet run --project tests/GitWork.UiChecks/GitWork.UiChecks.csproj
~~~

Build the self-contained installer and update feed:

~~~powershell
./tools/Build-Release.ps1 -Version 0.1.0
~~~

See [Releasing](docs/RELEASING.md) for the draft-release workflow and the files that must be published together. Building does not publish anything.

## Settings and privacy

Settings are stored locally under %LOCALAPPDATA%\GitWork. Existing settings from WorktreeDesk are imported when the new settings file does not exist. Screenshots use demonstration repositories, not private projects. Build output, release packages and local settings do not belong in Git.

## Author and license

Created by [eddchervov](https://github.com/eddchervov). [Project on GitHub](https://github.com/eddchervov/GitWork).

Released under the [MIT License](LICENSE).