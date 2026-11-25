# Build a Velopack release (Setup.exe + portable zip) for RcloneMount.
# Usage (from repo root):
#   .\scripts\release.ps1 -SuggestOnly    # print the next version from CHANGELOG.md, change nothing
#   .\scripts\release.ps1                 # release the suggested version
#   .\scripts\release.ps1 -Version 1.2.0  # override the suggestion
#   .\scripts\release.ps1 -Upload         # also publish to GitHub (env GITHUB_TOKEN; -RepoUrl or env RCLONEMOUNT_REPO_URL)
#   .\scripts\release.ps1 -SelfTest       # check the version suggestion rules
#
# Version rules live in version.ps1. An empty [Unreleased] block means nothing to ship.
# build.ps1 -Mode Release calls this script; this script calls build.ps1 -Mode Local only.
#
# Output: dist\releases\ (RcloneMount-win-Setup.exe, RcloneMount-win-Portable.zip, packages)

[CmdletBinding()]
param(
    [string]$Version = "",
    [switch]$SuggestOnly,
    [switch]$Upload,
    [string]$RepoUrl = $env:RCLONEMOUNT_REPO_URL,
    [switch]$SelfTest,
    [switch]$SkipCleanCheck
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "version.ps1")

if ($SelfTest) {
    if (Test-SuggestedVersion) { exit 0 } else { exit 1 }
}

$repoRoot    = Resolve-Path (Join-Path $PSScriptRoot "..")
$csproj      = Join-Path $repoRoot "src\RcloneMount\RcloneMount.csproj"
$changelog   = Join-Path $repoRoot "CHANGELOG.md"
$icon        = Join-Path $repoRoot "src\RcloneMount\app.ico"
# Own folder, not bin\...\publish: no stale single-file leftovers, and a copy running from bin is never touched.
$publishDir  = Join-Path $repoRoot "dist\publish"
$releasesDir = Join-Path $repoRoot "dist\releases"
$utf8NoBom   = New-Object System.Text.UTF8Encoding $false

if (-not (Test-Path $csproj)) {
    throw "csproj not found at $csproj - run from the repo root."
}

# -- Resolve version ----------------------------------------------
$clText = [IO.File]::ReadAllText($changelog)
$base = Get-LastReleasedVersion $clText
$body = Get-UnreleasedBody $clText
$suggested = Get-SuggestedVersion $clText
if (-not $suggested) { throw "CHANGELOG.md [Unreleased] is empty - nothing to release." }
$projVersion = Get-CsprojVersion $csproj
if ($projVersion -ne $base) {
    Write-Warning "csproj <Version> is $projVersion but CHANGELOG.md's newest release is $base. Suggestion uses the changelog."
}
Write-Host "Suggested: $suggested (from [Unreleased] headings, base $base)" -ForegroundColor Cyan
if ($SuggestOnly) { exit 0 }

if (-not $Version) { $Version = $suggested }
if (-not (Test-VersionString $Version)) { throw "-Version must look like 1.2.3 or 1.2.3-tag (got '$Version')." }
Write-Host "Version:   $Version" -ForegroundColor Cyan

if ($Upload) {
    if (-not $RepoUrl) { throw "-Upload needs -RepoUrl or env RCLONEMOUNT_REPO_URL (e.g. https://github.com/owner/repo)." }
    if (-not $env:GITHUB_TOKEN) { throw "-Upload needs env GITHUB_TOKEN." }
}

# -- Warn on dirty tree (release from a dirty tree is a bug magnet) --
if (-not $SkipCleanCheck -and (Test-Path (Join-Path $repoRoot ".git"))) {
    Push-Location $repoRoot
    try {
        $status = git status --porcelain 2>$null
        if ($LASTEXITCODE -eq 0 -and $status) {
            Write-Warning "Working tree is not clean. Continuing - pass -SkipCleanCheck to silence."
            $status | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkYellow }
        }
    } finally { Pop-Location }
}

$notesFile = Join-Path ([IO.Path]::GetTempPath()) "RcloneMount-$Version-notes.md"
[IO.File]::WriteAllText($notesFile, $body, $utf8NoBom)

# Nothing in the repo changes until build, publish and pack have all succeeded, so a
# failed release leaves csproj and CHANGELOG exactly as they were.

# -- Build + test (version passed as -p:Version) ------------------
# Never -Mode Release here: build.ps1 -Mode Release calls this script.
& (Join-Path $PSScriptRoot "build.ps1") -Mode Local -Version $Version
if (-not $?) { throw "build.ps1 failed." }

# -- Publish self-contained folder (Velopack packs the folder) ----
Write-Host "Publishing..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& dotnet publish $csproj -c Release -r win-x64 --self-contained --nologo -o $publishDir "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit $LASTEXITCODE." }
# Strip the debug symbols file - no reason to ship it to end users.
Remove-Item (Join-Path $publishDir "RcloneMount.pdb") -ErrorAction SilentlyContinue
if (-not (Test-Path (Join-Path $publishDir "RcloneMount.exe"))) { throw "Missing RcloneMount.exe in $publishDir after publish." }

# -- Package -------------------------------------------------------
# Velopack's own download -> pack -> upload flow: pack in a staging folder seeded with the
# previous release (so a delta package is made), then copy into dist\releases keeping only
# the newest 3 releases. Velopack rewrites the feed file, so it never lists deleted packages.
$packDir = Join-Path $repoRoot "dist\pack"
if (Test-Path $packDir) { Remove-Item $packDir -Recurse -Force }
if (Test-Path (Join-Path $releasesDir "releases.win.json")) {
    & vpk download local --path $releasesDir -o $packDir
    if ($LASTEXITCODE -ne 0) { throw "vpk download local failed with exit $LASTEXITCODE." }
}
Write-Host "Packing..." -ForegroundColor Cyan
& vpk pack --packId RcloneMount --packVersion $Version --runtime win-x64 --packDir $publishDir --mainExe RcloneMount.exe `
    --icon $icon --packTitle RcloneMount --releaseNotes $notesFile -o $packDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit $LASTEXITCODE." }
New-Item -ItemType Directory -Force -Path $releasesDir | Out-Null
& vpk upload local -o $packDir --path $releasesDir --keepMaxReleases 3
if ($LASTEXITCODE -ne 0) { throw "vpk upload local failed with exit $LASTEXITCODE." }

if ($Upload) {
    # GitHub releases are never pruned here - deleting public releases is the owner's call.
    Write-Host "Uploading to $RepoUrl..." -ForegroundColor Cyan
    & vpk upload github -o $packDir --repoUrl $RepoUrl --token $env:GITHUB_TOKEN --publish
    if ($LASTEXITCODE -ne 0) { throw "vpk upload failed with exit $LASTEXITCODE." }
}

# -- Stamp csproj + CHANGELOG (only after success) ------------------
$projText = [IO.File]::ReadAllText($csproj)
[IO.File]::WriteAllText($csproj, [regex]::Replace($projText,'<Version>[^<]*</Version>', "<Version>$Version</Version>"), $utf8NoBom)

$nl = "`n"
if ($clText.Contains("`r`n")) { $nl = "`r`n" }
$section = "## [$Version] - $(Get-Date -Format yyyy-MM-dd)"
$newBlock = "## [Unreleased]$nl$nl$section$nl$nl$body$nl$nl"
# Same block Get-UnreleasedBody reads, heading included.
$u = [regex]::Match($clText, '(?ms)^## \[Unreleased\][^\n]*\n.*?(?=^## \[|\z)')
$clText = $clText.Remove($u.Index, $u.Length).Insert($u.Index, $newBlock)
[IO.File]::WriteAllText($changelog, $clText, $utf8NoBom)
Write-Host "Stamped csproj + CHANGELOG.md with $Version" -ForegroundColor DarkGray

Write-Host ""
Get-ChildItem $releasesDir -Filter "RcloneMount-win-*" | ForEach-Object {
    Write-Host ("Built:    {0}  ({1} MB)" -f $_.FullName, [math]::Round($_.Length / 1MB, 1)) -ForegroundColor Green
}
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. git add CHANGELOG.md src/RcloneMount/RcloneMount.csproj"
Write-Host "  2. git commit -m `"Release v$Version`""
Write-Host "  3. git tag v$Version ; git push ; git push --tags"
if (-not $Upload) { Write-Host "  4. Publish: vpk upload github -o dist\releases --repoUrl <url> --token <token> --publish" }
