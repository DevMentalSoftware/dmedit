# release.ps1 — Tag and push a release to trigger the GitHub Actions workflow.
#
# Usage:
#   .\release.ps1              # Next beta (e.g. v0.5.226-beta.6)
#   .\release.ps1 -Public      # Public release (e.g. v0.5.226)
#   .\release.ps1 -Major       # Bump major (e.g. v1.0.226-beta.1)
#   .\release.ps1 -Minor       # Bump minor (e.g. v0.6.226-beta.1)
#   .\release.ps1 -DryRun      # Show what would happen without doing it

param(
    [switch]$Public,
    [switch]$Major,
    [switch]$Minor,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# Ensure working tree is clean.
$status = git status --porcelain
if ($status) {
    Write-Error "Working tree is not clean. Commit or stash changes first."
    return
}

# Ensure we're up to date with remote.
git fetch origin --tags 2>$null

$localHead = git rev-parse HEAD
$remoteHead = git rev-parse origin/main 2>$null
if ($localHead -ne $remoteHead) {
    Write-Warning "Local HEAD ($($localHead.Substring(0,7))) differs from origin/main ($($remoteHead.Substring(0,7)))."
    $answer = Read-Host "Continue anyway? (y/N)"
    if ($answer -ne 'y') { return }
}

# Get commit count for the patch number.
$commitCount = [int](git rev-list --count HEAD)

# Find the latest tag to determine current major.minor and beta number.
$lastTag = git tag --sort=-v:refname | Select-Object -First 1
if ($lastTag -match '^v(\d+)\.(\d+)\.\d+(?:-beta\.(\d+))?$') {
    $curMajor = [int]$Matches[1]
    $curMinor = [int]$Matches[2]
    $curBeta  = if ($Matches[3]) { [int]$Matches[3] } else { 0 }
} else {
    Write-Error "Cannot parse last tag: $lastTag"
    return
}

# Determine new version.
$newMajor = $curMajor
$newMinor = $curMinor

if ($Major) {
    $newMajor++
    $newMinor = 0
    $curBeta = 0  # reset beta
} elseif ($Minor) {
    $newMinor++
    $curBeta = 0  # reset beta
}

if ($Public) {
    $tag = "v$newMajor.$newMinor.$commitCount"
} else {
    $newBeta = $curBeta + 1
    # Reset beta if major or minor changed.
    if ($Major -or $Minor) { $newBeta = 1 }
    $tag = "v$newMajor.$newMinor.$commitCount-beta.$newBeta"
}

Write-Host ""
Write-Host "  Last tag:  $lastTag"
Write-Host "  New tag:   $tag"
Write-Host "  Commits:   $commitCount"
Write-Host ""

if ($DryRun) {
    Write-Host "(dry run — no changes made)" -ForegroundColor Yellow
    return
}

$confirm = Read-Host "Tag and push '$tag'? (y/N)"
if ($confirm -ne 'y') {
    Write-Host "Cancelled."
    return
}

git tag $tag
git push origin $tag

Write-Host ""
Write-Host "Pushed $tag — GitHub Actions release workflow will start shortly." -ForegroundColor Green
Write-Host "https://github.com/DevMentalSoftware/dmedit/actions" -ForegroundColor Cyan
