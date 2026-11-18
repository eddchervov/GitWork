# Releasing Git Work

Git Work distributes Velopack packages through the existing [eddchervov/GitWork](https://github.com/eddchervov/GitWork) GitHub repository and its stable Windows x64 release feed. No separate update server or application token is required.

## Build and review

1. Set the three-part version in `Directory.Build.props` and update `RELEASE_NOTES.md`.
2. On Windows, build locally with .NET SDK 10 and Git for Windows:

   ```powershell
   ./tools/Build-Release.ps1 -Version 0.4.1
   ```

3. Review the generated packages and feed files under `artifacts/releases/0.4.1`.
4. Upload every generated file to the matching GitHub release. The stable update feed requires the feed JSON and full package; Setup.exe alone is not enough for in-app updates.

The release includes the generated stable feed files `assets.win-x64-stable.json`, `releases.win-x64-stable.json`, and `RELEASES-win-x64-stable`, plus the `.nupkg` package, Setup, Portable package, and `SHA256SUMS.txt`. Keep the generated filenames intact.

## Publish

GitHub Actions has a manually triggered build workflow. It builds downloadable artifacts by default. Its optional `create_draft` input creates a draft GitHub release for review and does not publish it. Publishing a release is a separate action after the files and release notes have been reviewed.

The application checks the stable Windows x64 channel at most once per day. Users can also select **Help → Check for updates**. Downloading and restarting require an explicit user action. Updates wait until running Git operations have finished. Release notes are displayed as text.

Update checks use the public `releases/latest/download/releases.win-x64-stable.json` asset instead of the anonymous GitHub API. Velopack then uses the selected release's version-specific `releases/download/vX.Y.Z/` URL for both its feed and package download. This avoids API rate limits and keeps an in-progress download tied to the version the user chose. An unsigned installer is a separate Windows trust issue, not a reason for an HTTP 403 update-check failure.

## Distribution

- Give users the generated Setup executable; it includes the .NET runtime.
- Git for Windows must be installed separately.
- A source-build or Portable copy can link to Setup, but does not support Velopack's in-place update mechanism until installed through Setup.
- Builds are unsigned until a signing certificate is configured. Signing can be added to the packaging step; certificates and passwords belong in CI secrets.
