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

# Ensure we're on main.
$branch = git rev-parse --abbrev-ref HEAD
if ($branch -ne 'main') {
    Write-Error "Not on main branch (currently on '$branch')."
    return
}

# Fetch latest from remote.
git fetch origin --tags 2>$null

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

# Check for unpushed commits.
$unpushed = git log origin/main..HEAD --oneline
$unpushedCount = ($unpushed | Measure-Object).Count

Write-Host ""
Write-Host "  Last tag:        $lastTag"
Write-Host "  New tag:         $tag"
Write-Host "  Total commits:   $commitCount"
if ($unpushedCount -gt 0) {
    Write-Host "  Unpushed:        $unpushedCount commit(s)" -ForegroundColor Yellow
}
Write-Host ""

if ($DryRun) {
    if ($unpushedCount -gt 0) {
        Write-Host "Unpushed commits:"
        $unpushed | ForEach-Object { Write-Host "  $_" }
    }
    Write-Host "(dry run — no changes made)" -ForegroundColor Yellow
    return
}

$confirm = Read-Host "Tag and push '$tag'? (y/N)"
if ($confirm -ne 'y') {
    Write-Host "Cancelled."
    return
}

# Push commits first (so the tagged commit exists on the remote),
# then create and push the tag.
if ($unpushedCount -gt 0) {
    Write-Host "Pushing $unpushedCount commit(s) to origin/main..."
    git push origin main
}

git tag $tag
git push origin $tag

Write-Host ""
Write-Host "Pushed $tag — GitHub Actions release workflow will start shortly." -ForegroundColor Green
Write-Host "https://github.com/DevMentalSoftware/dmedit/actions" -ForegroundColor Cyan
