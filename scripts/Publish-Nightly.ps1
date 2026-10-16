param(
    [Parameter(Mandatory)][ValidatePattern('^[^/\s]+/[^/\s]+$')][string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version,
    [string]$PackageDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/packages'),
    [string]$Branch = 'master',
    [string]$BuildUrl = "https://github.com/$Repository/actions/runs/$env:GITHUB_RUN_ID"
)
$ErrorActionPreference = 'Stop'

function Invoke-GitHub([string[]]$Arguments) {
    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI failed: gh $($Arguments[0]) $($Arguments[1])" }
    return $output
}

function Test-CurrentHead {
    $head = (Invoke-GitHub @('api', "repos/$Repository/git/ref/heads/$Branch", '--jq', '.object.sha')) -join "`n"
    return $head.Trim() -eq $Commit
}

function Get-NightlyRelease {
    $json = (Invoke-GitHub @('api', "repos/$Repository/releases?per_page=100", '--paginate', '--jq', '.[] | select(.tag_name == "nightly")')) -join "`n"
    if ($json.Trim()) { return $json | ConvertFrom-Json }
    return $null
}

# A rerun of an old commit must not change the release, tag or any of its assets.
if (!(Test-CurrentHead)) {
    Write-Host "Skipping nightly: $Commit is no longer the head of $Branch."
    return
}

$staging = Join-Path $PackageDirectory 'nightly'
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$files = @(
    @{ Source = "Unread-$Version-win-x64.zip"; Name = 'Unread-win-x64.zip' },
    @{ Source = "Unread-$Version-win-x64-setup.exe"; Name = 'Unread-win-x64-setup.exe' }
)
foreach ($file in $files) {
    $source = Join-Path $PackageDirectory $file.Source
    if (!(Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -eq 0) {
        throw "Missing or empty nightly package: $source"
    }
    $file.Path = Join-Path $staging $file.Name
    Copy-Item -LiteralPath $source -Destination $file.Path -Force
    $file.Hash = (Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $file.Size = (Get-Item -LiteralPath $file.Path).Length
}
$notesPath = Join-Path $staging 'notes.md'
@(
    'Latest nightly build',
    '',
    "Commit: [$Commit](https://github.com/$Repository/commit/$Commit) on $Branch.",
    "Build: [$Version]($BuildUrl).", '',
    'Download the installer or the portable ZIP separately below.', '',
    '| File | SHA-256 |', '| --- | --- |',
    ($files | ForEach-Object { "| $($_.Name) | $($_.Hash) |" })
) | Set-Content -LiteralPath $notesPath -Encoding utf8NoBOM

$release = Get-NightlyRelease
if ($release -and $release.immutable) { throw 'The nightly release is immutable and cannot be updated.' }

# Keep the public release from exposing a mixture of old and new packages.
# Workflow concurrency lets an in-flight update finish rather than cancelling its uploads.
if ($release) {
    $null = Invoke-GitHub @('release', 'edit', 'nightly', '--repo', $Repository, '--draft=true')
}
$tagSha = (Invoke-GitHub @('api', "repos/$Repository/git/matching-refs/tags/nightly", '--jq', '.[] | select(.ref == "refs/tags/nightly") | .object.sha')) -join "`n"
if ($tagSha.Trim()) {
    $null = Invoke-GitHub @('api', '--method', 'PATCH', "repos/$Repository/git/refs/tags/nightly", '-f', "sha=$Commit", '-F', 'force=true', '--silent')
}
else {
    $null = Invoke-GitHub @('api', '--method', 'POST', "repos/$Repository/git/refs", '-f', 'ref=refs/tags/nightly', '-f', "sha=$Commit", '--silent')
}
if (!$release) {
    $null = Invoke-GitHub @('release', 'create', 'nightly', '--repo', $Repository, '--draft', '--prerelease', '--latest=false', '--verify-tag', '--target', $Commit, '--title', 'Nightly build', '--notes-file', $notesPath)
    $release = Get-NightlyRelease
    if (!$release) { throw 'The nightly draft was not created.' }
}

# Delete every previous asset, including older versioned filenames and checksum files.
$assetIds = @(Invoke-GitHub @('api', "repos/$Repository/releases/$($release.id)/assets", '--paginate', '--jq', '.[].id'))
foreach ($assetId in $assetIds) {
    $null = Invoke-GitHub @('api', '--method', 'DELETE', "repos/$Repository/releases/assets/$assetId", '--silent')
}
$null = Invoke-GitHub @('release', 'upload', 'nightly', $files[0].Path, $files[1].Path, '--repo', $Repository)

$assets = @(((Invoke-GitHub @('api', "repos/$Repository/releases/$($release.id)/assets")) -join "`n") | ConvertFrom-Json)
if ($assets.Count -ne $files.Count) { throw 'The nightly release must contain exactly two package assets.' }
foreach ($file in $files) {
    $asset = $assets | Where-Object name -EQ $file.Name
    if (!$asset -or $asset.state -ne 'uploaded' -or $asset.size -ne $file.Size -or $asset.digest -ne "sha256:$($file.Hash)") {
        throw "Nightly upload verification failed: $($file.Name)"
    }
}
if (!(Test-CurrentHead)) {
    Write-Host 'A newer commit arrived during upload. Keeping the release unpublished for the next build.'
    return
}
$null = Invoke-GitHub @('release', 'edit', 'nightly', '--repo', $Repository, '--draft=false', '--prerelease', '--latest=false', '--target', $Commit, '--title', 'Nightly build', '--notes-file', $notesPath)
Write-Host "Published https://github.com/$Repository/releases/tag/nightly at $Commit"
