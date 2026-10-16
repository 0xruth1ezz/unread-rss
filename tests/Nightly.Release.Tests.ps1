# No network or GitHub writes: gh is shadowed with a stateful in-process test double.
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$publisher = Join-Path $repositoryRoot 'scripts/Publish-Nightly.ps1'
$fixture = Join-Path $repositoryRoot "artifacts/nightly-tests/$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$commit = 'a' * 40
$zip = Join-Path $fixture 'Unread-0.1.1-win-x64.zip'
$installer = Join-Path $fixture 'Unread-0.1.1-win-x64-setup.exe'
'ZIP fixture' | Set-Content -LiteralPath $zip
'Installer fixture' | Set-Content -LiteralPath $installer

function Assert($Condition, [string]$Message) {
    if (!$Condition) { throw "Assertion failed: $Message" }
}

function Reset-State([bool]$Existing = $false) {
    $global:nightlyReleaseTestState = @{
        Head = $commit; Tag = ''; Release = $null
        Assets = [System.Collections.Generic.List[object]]::new()
        Deleted = [System.Collections.Generic.List[string]]::new()
        Publications = [System.Collections.Generic.List[object]]::new()
        AdvanceDuringUpload = $false; CorruptDigest = $false; FailUpload = $false
        ReleaseLookups = 0
    }
    if ($Existing) {
        $global:nightlyReleaseTestState.Tag = 'b' * 40
        $global:nightlyReleaseTestState.Release = @{ id = 27; tag_name = 'nightly'; draft = $false; immutable = $false }
        foreach ($name in @('old-build.zip', 'old-setup.exe', 'old-checksums.txt')) {
            $global:nightlyReleaseTestState.Assets.Add([pscustomobject]@{ id = 100 + $global:nightlyReleaseTestState.Assets.Count; name = $name })
        }
    }
}

function gh {
    $arguments = @($args)
    $state = $global:nightlyReleaseTestState
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq 'api') {
        $endpoint = $arguments | Where-Object { $_ -like 'repos/*' } | Select-Object -First 1
        $methodIndex = [array]::IndexOf($arguments, '--method')
        $method = if ($methodIndex -ge 0) { $arguments[$methodIndex + 1] } else { 'GET' }
        if ($endpoint -eq 'repos/owner/repo/git/ref/heads/master') { return $state.Head }
        if ($endpoint -eq 'repos/owner/repo/releases?per_page=100') {
            $state.ReleaseLookups++
            if ($state.Release) { return ConvertTo-Json -InputObject $state.Release -Compress }
            return
        }
        if ($endpoint -eq 'repos/owner/repo/releases' -and $method -eq 'POST') {
            $inputIndex = [array]::IndexOf($arguments, '--input')
            $payload = Get-Content -LiteralPath $arguments[$inputIndex + 1] -Raw | ConvertFrom-Json
            Assert ($state.Tag -eq $commit -and $payload.target_commitish -eq $commit) 'A new draft uses the exact built commit.'
            Assert ($payload.draft -and $payload.prerelease) 'A new nightly starts as a prerelease draft.'
            $state.Release = @{ id = 27; tag_name = 'nightly'; draft = $true; immutable = $false }
            return ConvertTo-Json -InputObject $state.Release -Compress
        }
        if ($endpoint -eq 'repos/owner/repo/git/matching-refs/tags/nightly') { return $state.Tag }
        if ($endpoint -in @('repos/owner/repo/git/refs', 'repos/owner/repo/git/refs/tags/nightly')) {
            Assert ($method -in @('POST', 'PATCH')) 'Tag updates use an explicit mutation method.'
            Assert (!$state.Release -or $state.Release.draft) 'A published release is hidden before its tag moves.'
            $state.Tag = ($arguments | Where-Object { $_ -like 'sha=*' }) -replace '^sha=', ''
            return
        }
        if ($endpoint -eq 'repos/owner/repo/releases/27/assets') {
            if ($arguments -contains '.[].id') { return $state.Assets | ForEach-Object id }
            return ConvertTo-Json -InputObject $state.Assets.ToArray() -Depth 5 -Compress
        }
        if ($endpoint -match '^repos/owner/repo/releases/assets/(\d+)$' -and $method -eq 'DELETE') {
            Assert $state.Release.draft 'Old assets are removed only while the release is a draft.'
            $asset = $state.Assets | Where-Object id -EQ $Matches[1]
            $state.Deleted.Add($asset.name)
            $null = $state.Assets.Remove($asset)
            return
        }
    }
    if ($arguments[0] -eq 'release') {
        switch ($arguments[1]) {
            'edit' {
                if ($arguments -contains '--draft=true') { $state.Release.draft = $true }
                if ($arguments -contains '--draft=false') {
                    Assert ($state.Head -eq $commit) 'A stale commit is never published.'
                    $state.Release.draft = $false
                    $state.Publications.Add(@{ Tag = $state.Tag; Names = @($state.Assets | ForEach-Object name) })
                }
                return
            }
            'upload' {
                Assert $state.Release.draft 'Uploads happen while the release is a draft.'
                if ($state.FailUpload) { $global:LASTEXITCODE = 1; return }
                foreach ($path in $arguments | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }) {
                    $item = Get-Item -LiteralPath $path
                    $digest = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                    $state.Assets.Add([pscustomobject]@{ id = 200 + $state.Assets.Count; name = $item.Name; size = $item.Length; state = 'uploaded'; digest = "sha256:$digest" })
                }
                if ($state.AdvanceDuringUpload) { $state.Head = 'b' * 40 }
                if ($state.CorruptDigest) { $state.Assets[0].digest = 'sha256:wrong' }
                return
            }
        }
    }
    throw "Unexpected gh call in test: $($arguments -join ' ')"
}

function Publish-Fixture {
    & $publisher -Repository owner/repo -Commit $commit -Version 0.1.1 -PackageDirectory $fixture -BuildUrl https://github.com/owner/repo/actions/runs/1
}

function Expect-Failure([string]$MessagePattern) {
    $failed = $false
    try { Publish-Fixture }
    catch { $failed = $true; Assert ($_.Exception.Message -like $MessagePattern) "Expected failure $MessagePattern, got $($_.Exception.Message)" }
    Assert $failed 'Publication should have failed.'
}

try {
    Reset-State
    Publish-Fixture
    Assert ($global:nightlyReleaseTestState.Publications.Count -eq 1) 'The first nightly is published.'
    Assert ($global:nightlyReleaseTestState.Tag -eq $commit) 'The tag targets the built commit.'
    Assert ($global:nightlyReleaseTestState.ReleaseLookups -eq 1) 'Creation uses its API response, without a potentially stale draft lookup.'
    Assert (($global:nightlyReleaseTestState.Assets.name | Sort-Object) -join ',' -eq 'Unread-win-x64-setup.exe,Unread-win-x64.zip') 'Exactly the ZIP and installer are attached separately.'
    Assert ((Get-Content -LiteralPath (Join-Path $fixture 'nightly/notes.md') -Raw) -match $commit) 'Release notes identify the exact commit.'

    Reset-State $true
    Publish-Fixture
    Assert ($global:nightlyReleaseTestState.Deleted.Count -eq 3) 'All previous assets, including old checksum files, are removed.'
    Assert ($global:nightlyReleaseTestState.Assets.Count -eq 2) 'Only current packages remain.'
    Assert ($global:nightlyReleaseTestState.Release.id -eq 27) 'The existing release is reused.'
    Publish-Fixture
    Assert ($global:nightlyReleaseTestState.Assets.Count -eq 2) 'Rerunning the same commit does not accumulate assets.'

    Reset-State $true
    $global:nightlyReleaseTestState.Head = 'b' * 40
    Publish-Fixture
    Assert (!$global:nightlyReleaseTestState.Release.draft -and $global:nightlyReleaseTestState.Deleted.Count -eq 0) 'An old run leaves the current release untouched.'

    Reset-State $true
    $global:nightlyReleaseTestState.AdvanceDuringUpload = $true
    Publish-Fixture
    Assert ($global:nightlyReleaseTestState.Release.draft -and $global:nightlyReleaseTestState.Publications.Count -eq 0) 'A newer head during upload prevents publication.'

    Reset-State $true
    $global:nightlyReleaseTestState.CorruptDigest = $true
    Expect-Failure '*upload verification failed*'
    Assert $global:nightlyReleaseTestState.Release.draft 'A damaged upload is never published.'

    Reset-State $true
    $global:nightlyReleaseTestState.FailUpload = $true
    Expect-Failure '*GitHub CLI failed*'
    Assert ($global:nightlyReleaseTestState.Publications.Count -eq 0) 'A CLI failure is not reported as publication success.'

    Reset-State $true
    $global:nightlyReleaseTestState.Release.immutable = $true
    Expect-Failure '*immutable*'
    Assert (!$global:nightlyReleaseTestState.Release.draft -and $global:nightlyReleaseTestState.Deleted.Count -eq 0) 'An immutable release is not altered.'

    Reset-State $true
    Set-Content -LiteralPath $zip -Value '' -NoNewline
    Expect-Failure '*Missing or empty*'
    Assert (!$global:nightlyReleaseTestState.Release.draft -and $global:nightlyReleaseTestState.Deleted.Count -eq 0) 'Incomplete packages do not disturb the current release.'

    Write-Host 'PASS: 9 nightly scenarios — create, replace, rerun, stale run, superseded upload, checksum failure, CLI failure, immutable release and missing package.'
}
finally {
    Remove-Variable -Name nightlyReleaseTestState -Scope Global
    $global:LASTEXITCODE = 0
}
