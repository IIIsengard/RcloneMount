# RcloneMount build entry point.
#
# Run with no arguments for a menu:
#   1) Full build + Velopack publish  -> release.ps1 (bumps csproj + CHANGELOG, builds
#      Setup.exe + portable zip in dist\releases, optional GitHub upload)
#   2) Local test build               -> compile + tests, stamped with a version, runnable
#      copy in dist\local\<version>\; nothing published, csproj and CHANGELOG untouched
#
# Non-interactive:
#   .\scripts\build.ps1 -Mode Local                         # version = <suggested>-local.<yyyyMMddHHmm>
#   .\scripts\build.ps1 -Mode Local -Version 1.1.0-rc.1
#   .\scripts\build.ps1 -Mode Release -Version 1.1.0 [-Upload]
#   .\scripts\build.ps1 -SelfTest                           # version-rule self-check
#
# Exits non-zero on any failure, so release.ps1 and CI can chain on it.

[CmdletBinding()]
param(
    [ValidateSet("", "Local", "Release")]
    [string]$Mode = "",
    [string]$Version = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Upload,
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"
$repoRoot  = Resolve-Path (Join-Path $PSScriptRoot "..")
$sln       = Join-Path $repoRoot "RcloneMount.sln"
$csproj    = Join-Path $repoRoot "src\RcloneMount\RcloneMount.csproj"
$changelog = Join-Path $repoRoot "CHANGELOG.md"
. (Join-Path $PSScriptRoot "version.ps1")

# Menu runs are often started from Explorer, whose window closes the moment the script
# ends: show the error and wait for Enter instead of vanishing.
$interactive = (-not $Mode) -and (-not $SelfTest)
trap {
    Write-Host ""
    Write-Host "BUILD FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($interactive) { [void](Read-Host "Press Enter to close") }
    exit 1
}
function Wait-Close { if ($interactive) { [void](Read-Host "Press Enter to close") } }

if ($SelfTest) {
    if (Test-SuggestedVersion) { exit 0 } else { exit 1 }
}

$changelogText = Get-Content $changelog -Raw
$suggested     = Get-SuggestedVersion $changelogText
$lastRelease   = Get-LastReleasedVersion $changelogText
$csprojVersion = Get-CsprojVersion $csproj

function Read-Version([string]$Default) {
    while ($true) {
        $answer = Read-Host "Version [$Default]"
        if (-not $answer) { $answer = $Default }
        if (Test-VersionString $answer) { return $answer }
        Write-Host "  '$answer' is not x.y.z or x.y.z-tag." -ForegroundColor Yellow
    }
}

# -- Menu (only when no -Mode given) -------------------------------
if ($interactive) {
    Write-Host ""
    Write-Host "RcloneMount build" -ForegroundColor Cyan
    Write-Host "  Last release: $lastRelease   csproj: $csprojVersion   suggested next: $(if ($suggested) { $suggested } else { '(nothing in [Unreleased])' })"
    Write-Host ""
    Write-Host "  1) Full build + Velopack publish (Setup.exe + portable, optional GitHub upload)"
    Write-Host "  2) Local test build (compile + tests, version-stamped, nothing published)"
    Write-Host "  Q) Quit"
    $choice = (Read-Host "Choose [1/2/Q]").Trim().ToUpperInvariant()
    switch ($choice) {
        "1" { $Mode = "Release" }
        "2" { $Mode = "Local" }
        default { Write-Host "Nothing done."; exit 0 }
    }
}

$stamp = Get-Date -Format "yyyyMMddHHmm"
$baseForLocal = $suggested
if (-not $baseForLocal) { $baseForLocal = $lastRelease }
$defaultVersion = $(if ($Mode -eq "Release") { $suggested } else { "$baseForLocal-local.$stamp" })

if (-not $Version) {
    if ($interactive) {
        if (-not $defaultVersion) { $defaultVersion = $lastRelease }
        $Version = Read-Version $defaultVersion
    } else {
        $Version = $defaultVersion
    }
}
if (-not $Version) { throw "No version: [Unreleased] in CHANGELOG.md is empty. Pass -Version." }
if (-not (Test-VersionString $Version)) { throw "'$Version' is not a valid version (x.y.z or x.y.z-tag)." }

# -- Both modes build into bin\ -------------------------------------
# A copy running from bin\ locks RcloneMount.exe and the build fails with MSB3027.
# Never kill it (it may have drives mounted) - ask the user to exit it from the tray.
$binDir = Join-Path $repoRoot "src\RcloneMount\bin"
while ($true) {
    $locking = @(Get-Process RcloneMount -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($binDir, [StringComparison]::OrdinalIgnoreCase) })
    if ($locking.Count -eq 0) { break }
    $list = ($locking | ForEach-Object { "PID $($_.Id): $($_.Path)" }) -join "`n  "
    if (-not $interactive) { throw "RcloneMount is running from the build folder and locks it:`n  $list`nExit it from the tray, then rebuild." }
    Write-Host "RcloneMount is running from the build folder and locks it:`n  $list" -ForegroundColor Yellow
    if ((Read-Host "Exit it from the tray, then press Enter to retry (Q to quit)").Trim().ToUpperInvariant() -eq "Q") { throw "Cancelled: RcloneMount still running from the build folder." }
}

# -- 1) Full build + Velopack publish -------------------------------
if ($Mode -eq "Release") {
    if ($interactive -and -not $Upload) {
        $Upload = (Read-Host "Upload to GitHub too? [y/N]").Trim().ToLowerInvariant() -eq "y"
    }
    $releaseArgs = @{ Version = $Version }
    if ($Upload) { $releaseArgs.Upload = $true }
    & (Join-Path $PSScriptRoot "release.ps1") @releaseArgs
    if (-not $?) { throw "release.ps1 failed." }
    Wait-Close
    exit 0
}

# -- 2) Local test build ------------------------------------------
Write-Host "Building $Version ($Configuration)..." -ForegroundColor Cyan
& dotnet build $sln -c $Configuration --nologo "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit $LASTEXITCODE." }

if (-not $SkipTests) {
    Write-Host "Testing..." -ForegroundColor Cyan
    & dotnet test $sln -c $Configuration --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit $LASTEXITCODE." }
}

# Runnable copy per version, so test builds sit side by side and never lock bin\.
$localDir = Join-Path $repoRoot "dist\local\$Version"
Write-Host "Publishing runnable copy to $localDir..." -ForegroundColor Cyan
& dotnet publish $csproj -c $Configuration -r win-x64 --self-contained false --nologo "-p:Version=$Version" -o $localDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit $LASTEXITCODE." }

# Keep only the 3 newest local builds so dist\local doesn't pile up.
Get-ChildItem (Join-Path $repoRoot "dist\local") -Directory | Sort-Object LastWriteTime -Descending | Select-Object -Skip 3 | ForEach-Object {
    try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop; Write-Host "  removed old local build $($_.Name)" -ForegroundColor DarkGray }
    catch { Write-Warning "Could not remove old local build $($_.Name) (still running?)." }
}

$exe = Join-Path $localDir "RcloneMount.exe"
$info = (Get-Item $exe).VersionInfo
Write-Host ""
Write-Host "Build OK: $Version" -ForegroundColor Green
Write-Host "  run:          $exe"
Write-Host "  file version: $($info.FileVersion)   product version: $($info.ProductVersion)"
Write-Host "  Exit any running RcloneMount from the tray first - only one instance runs at a time."
Wait-Close
