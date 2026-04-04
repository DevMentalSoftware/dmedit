# release.ps1 — Tag and push a release to trigger the GitHub Actions workflow.
#
# Usage:
#   .\release.ps1              # Next beta (e.g. v0.5.226-beta.6)
#   .\release.ps1 -Public      # Public release (e.g. v0.5.226)
#   .\release.ps1 -Major       # Bump major (e.g. v1.0.226-beta.1)
#   .\release.ps1 -Minor       # Bump minor (e.g. v0.6.226-beta.1)
#   .\release.ps1 -DryRun      # Show what would happen without doing it
#
# All output is written to release.log in the repo root.

param(
    [switch]$Public,
    [switch]$Major,
    [switch]$Minor,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$logFile = Join-Path $repoRoot 'release.log'

function Log($msg) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $msg"
    Write-Host $line
    Add-Content -Path $logFile -Value $line
}

function Abort($msg) {
    Log "ERROR: $msg"
    Log ''
    Log 'Release aborted.'
    exit 1
}

# Start fresh log for this run.
Set-Content -Path $logFile -Value "=== DMEdit Release $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==="
Log ''

# Ensure working tree is clean.
$status = git status --porcelain
if ($status) { Abort 'Working tree is not clean. Commit or stash changes first.' }

# Ensure we're on main.
$branch = git rev-parse --abbrev-ref HEAD
if ($branch -ne 'main') { Abort "Not on main branch (currently on '$branch')." }

# Fetch latest from remote.
Log 'Fetching from origin...'
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
    Abort "Cannot parse last tag: $lastTag"
}

# Determine new version.
$newMajor = $curMajor
$newMinor = $curMinor

if ($Major) {
    $newMajor++
    $newMinor = 0
    $curBeta = 0
} elseif ($Minor) {
    $newMinor++
    $curBeta = 0
}

if ($Public) {
    $tag = "v$newMajor.$newMinor.$commitCount"
} else {
    $newBeta = $curBeta + 1
    if ($Major -or $Minor) { $newBeta = 1 }
    $tag = "v$newMajor.$newMinor.$commitCount-beta.$newBeta"
}

# Check for unpushed commits.
$unpushed = @(git log origin/main..HEAD --oneline)
$unpushedCount = $unpushed.Count

Log "Last tag:        $lastTag"
Log "New tag:         $tag"
Log "Total commits:   $commitCount"
Log "Unpushed:        $unpushedCount"
if ($unpushedCount -gt 0) {
    $unpushed | ForEach-Object { Log "  $_" }
}
Log ''

if ($DryRun) {
    Log '(dry run - no changes made)'
    Log "Log written to: $logFile"
    exit 0
}

# Push commits first so the tagged commit exists on the remote.
if ($unpushedCount -gt 0) {
    Log "Pushing $unpushedCount commit(s) to origin/main..."
    git push origin main 2>&1 | ForEach-Object { Log "  $_" }
}

Log "Tagging $tag..."
git tag $tag

Log "Pushing tag $tag..."
git push origin $tag 2>&1 | ForEach-Object { Log "  $_" }

Log ''
Log "Done. GitHub Actions release workflow will start shortly."
Log "https://github.com/DevMentalSoftware/dmedit/actions"
Log ''
Log "Log written to: $logFile"
