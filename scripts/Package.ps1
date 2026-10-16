param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = '1.0.0',
    [string]$IsccPath,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

if (!$IsccPath) {
    $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $compilerCandidates = @(
        $compilerCommand.Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
    )
    $IsccPath = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
}
if (!$IsccPath -or !(Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup compiler not found. Install Inno Setup 6.3 or newer, or pass -IsccPath <path-to-ISCC.exe>.'
}

& (Join-Path $PSScriptRoot 'Build.ps1') -Task Publish -Architecture x64 -Version $Version -NoRestore:$NoRestore
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }

$publishDirectory = Join-Path $projectRoot 'artifacts\Unread-win-x64'
$packageDirectory = Join-Path $projectRoot 'artifacts\packages'
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $publishDirectory -Force
foreach ($requiredFile in @('Unread.App.exe', 'Unread.App.dll', 'Unread.Core.dll', 'Unread.App.pri', 'App.xbf', 'MainWindow.xbf', 'Assets\Unread.ico')) {
    if (!(Test-Path -LiteralPath (Join-Path $publishDirectory $requiredFile) -PathType Leaf)) {
        throw "Publish is missing $requiredFile. Refusing to package an incomplete application."
    }
}

$zipPath = Join-Path $packageDirectory "Unread-$Version-win-x64.zip"
Compress-Archive -LiteralPath $publishDirectory -DestinationPath $zipPath -Force
& $IsccPath '/Qp' "/DAppVersion=$Version" "/DPublishDir=$publishDirectory" "/DPackageDir=$packageDirectory" (Join-Path $projectRoot 'packaging\Unread.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

$installerPath = Join-Path $packageDirectory "Unread-$Version-win-x64-setup.exe"
if (!(Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw 'Installer output was not created.' }
$hashes = foreach ($package in @($zipPath, $installerPath)) {
    $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $package)"
}
$hashes | Set-Content -LiteralPath (Join-Path $packageDirectory "Unread-$Version-SHA256SUMS.txt") -Encoding ascii
Write-Host "Installer, portable ZIP and SHA-256 checksums are in $packageDirectory"
