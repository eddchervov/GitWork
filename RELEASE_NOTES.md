# Git Work 0.3.2

- Fix preparation logs losing errors after the first 256 KB of output.
- Automatically save a full journal to the current user's Git Work log folder, independently of the on-screen output limit.
- Include attempts, step names, working directories, stdout/stderr, results and exit codes in the journal.
- Export the full journal instead of the text currently visible in the window; open the automatic log directly.
- Keep recent output in the window and the bounded step summary, while continuing to record later output and errors.
- Preserve secret masking and keep the journal available after closing the preparation window.
