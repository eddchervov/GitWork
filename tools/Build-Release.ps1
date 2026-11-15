param(
    [string] $Version,
    [switch] $SkipChecks
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml] $properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
    $Version = [string] $properties.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Use a stable three-part version, for example 0.1.0.'
}

$published = Join-Path $projectRoot "artifacts\publish\$Version"
$releases = Join-Path $projectRoot "artifacts\releases\$Version"
if (Test-Path -LiteralPath $published) {
    throw "Publish output already exists: $published. Use a new version or move the old output aside."
}
if (Get-ChildItem -LiteralPath $releases -Filter "*$Version*" -ErrorAction SilentlyContinue) {
    throw "Release $Version already exists in $releases."
}

Push-Location $projectRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
    if (-not $SkipChecks) {
        dotnet run --project tests/GitWork.Checks/GitWork.Checks.csproj -c Release -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
    }

    dotnet publish src/GitWork.App/GitWork.App.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -p:UseSharedCompilation=false -p:NuGetAudit=false -o $published
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }

    dotnet tool run vpk -- pack --packId GitWork --packTitle 'Git Work' --packAuthors eddchervov --packVersion $Version --packDir $published --mainExe GitWork.exe --runtime win-x64 --channel win-x64-stable --icon src/GitWork.App/Assets/App.ico --releaseNotes RELEASE_NOTES.md --outputDir $releases --skip-updates --yes
    if ($LASTEXITCODE -ne 0) { throw 'Installer packaging failed.' }

    Get-ChildItem -LiteralPath $releases -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    } | Set-Content -LiteralPath (Join-Path $releases 'SHA256SUMS.txt') -Encoding utf8
    Write-Host "Release $Version is ready in $releases"
}
finally {
    Pop-Location
}
