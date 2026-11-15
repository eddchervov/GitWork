# Releasing Git Work

Git Work uses Velopack packages hosted in **GitHub Releases**. No separate update server
or tokens inside the application are required.

## First release

1. Create **eddchervov/GitWork** on GitHub and upload the reviewed source.
2. Set the three-part version in **Directory.Build.props**, and update **RELEASE_NOTES.md**.
3. Run **./tools/Build-Release.ps1** on Windows with .NET SDK 10 and Git installed.
4. Review the installer and the files in **artifacts/releases/0.1.0**.
5. Create a draft release tagged **v0.1.0** and attach **all** release files, including
   the Velopack feed JSON and full package. Uploading only Setup.exe is insufficient
   for updates from inside the application.
6. Publish the draft when the release is ready for users.

The repository also includes a manually triggered GitHub Actions workflow.
It builds downloadable artifacts by default. The **create_draft** option creates
a draft release; it does not publish it.

## Following releases

Increase the version, build, and publish another release with all generated files.
The application checks the stable Windows x64 channel at most once per day
automatically. Users can also select **Help → Check for updates**.

The bell marks a newly available version. Downloading and restarting require an
explicit user action. Updates wait until running Git operations have finished.
Release notes are displayed as text.

## Distribution

- Give users the generated **Setup.exe**. It includes the .NET runtime.
- Git for Windows must be installed separately.
- A source-build/portable copy can link to the installer, but does not support
  Velopack's in-place update mechanism until installed through Setup.exe.
- Builds are unsigned until a signing certificate is configured. Signing can be
  added to the packaging step; certificates and passwords belong in CI secrets.

The update source is **https://github.com/eddchervov/GitWork**. Until that repository
and its first release exist, the application reports that no release source is
available rather than claiming it is up to date.

References: [Velopack WPF setup](https://docs.velopack.io/getting-started/csharp),
[GitHub update sources](https://docs.velopack.io/integrating/update-sources).
