# Shared version helpers, dot-sourced by build.ps1 and release.ps1:
#   . (Join-Path $PSScriptRoot "version.ps1")
#
# Next version comes from CHANGELOG.md (Keep a Changelog):
#   base = newest "## [x.y.z]" heading (not Unreleased)
#   [Unreleased] has "### Removed" or a line containing BREAKING  -> major
#   [Unreleased] has "### Added" or "### Changed"                  -> minor
#   anything else (Fixed, Security, ...)                           -> patch
#   empty [Unreleased]                                             -> $null (nothing to ship)

function Get-UnreleasedBody([string]$Changelog) {
    $m = [regex]::Match($Changelog, '(?ms)^## \[Unreleased\][^\n]*\n(.*?)(?=^## \[|\z)')
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return ""
}

function Get-LastReleasedVersion([string]$Changelog) {
    $m = [regex]::Match($Changelog, '(?m)^## \[(\d+\.\d+\.\d+)\]')
    if ($m.Success) { return $m.Groups[1].Value }
    return "0.0.0"
}

function Get-SuggestedVersion([string]$Changelog) {
    $body = Get-UnreleasedBody $Changelog
    if (-not $body) { return $null }
    $parts = (Get-LastReleasedVersion $Changelog).Split('.') | ForEach-Object { [int]$_ }
    if ($body -match '(?m)^### Removed' -or $body -cmatch 'BREAKING') {
        return "$($parts[0] + 1).0.0"
    }
    if ($body -match '(?m)^### (Added|Changed)') {
        return "$($parts[0]).$($parts[1] + 1).0"
    }
    return "$($parts[0]).$($parts[1]).$($parts[2] + 1)"
}

function Test-VersionString([string]$Version) {
    return $Version -match '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$'
}

function Get-CsprojVersion([string]$CsprojPath) {
    $xml = [xml](Get-Content $CsprojPath)
    return ($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
}

# Assert-based self-check; returns $true when every case passes.
function Test-SuggestedVersion {
    $base = "# Changelog`n`n## [Unreleased]`n`n{0}`n`n## [1.0.1] - 2026-07-16`n`n### Fixed`n- old`n"
    $cases = @(
        @{ Name = "fixed only -> patch";   Body = "### Fixed`n- a";               Want = "1.0.2" },
        @{ Name = "added -> minor";        Body = "### Fixed`n- a`n### Added`n- b"; Want = "1.1.0" },
        @{ Name = "changed -> minor";      Body = "### Changed`n- a";             Want = "1.1.0" },
        @{ Name = "removed -> major";      Body = "### Removed`n- a";             Want = "2.0.0" },
        @{ Name = "BREAKING -> major";     Body = "### Changed`n- BREAKING: x";   Want = "2.0.0" },
        @{ Name = "empty -> nothing";      Body = "";                             Want = $null }
    )
    $ok = $true
    foreach ($c in $cases) {
        $got = Get-SuggestedVersion ($base -f $c.Body)
        if ($got -eq $c.Want) { Write-Host "PASS  $($c.Name)" -ForegroundColor Green }
        else { Write-Host "FAIL  $($c.Name): got '$got', want '$($c.Want)'" -ForegroundColor Red; $ok = $false }
    }
    foreach ($v in @("1.2.3", "1.2.3-local.20261124")) {
        if (-not (Test-VersionString $v)) { Write-Host "FAIL  valid version rejected: $v" -ForegroundColor Red; $ok = $false }
    }
    foreach ($v in @("1.2", "v1.2.3", "1.2.3-")) {
        if (Test-VersionString $v) { Write-Host "FAIL  invalid version accepted: $v" -ForegroundColor Red; $ok = $false }
    }
    return $ok
}
