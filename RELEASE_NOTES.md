# Git Work 0.3.0

- Replace rounded, oversized panels with a compact flat interface and restrained dark and light themes.
- Keep the repository, branch selector and Fetch/Pull/Push actions in a compact header; retain IDE, terminal and folder shortcuts.
- Show change counts only for repositories with uncommitted files, without clean-state badges.
- Use a tall searchable branch panel anchored on the left, with the current branch pinned and local and remote branches in one list.
- Put the commit graph and message/author/date/hash table above the selected commit's files and diff.
- Search commits while preserving the query across refreshes; clearing a query restores the selected commit.
- Add C# syntax highlighting and addition/deletion counts while preserving selectable diff text.
- Validate the actual Push destination immediately before sending and reject changed or multiple push destinations; use normal non-forced pushes.
