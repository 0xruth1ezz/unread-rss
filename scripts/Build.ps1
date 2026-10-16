param(
    [ValidateSet('Build', 'Run', 'Test', 'Publish')][string]$Task = 'Run',
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = '1.0.0',
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$hasNet10 = $dotnetCommand -and ((& $dotnetCommand.Source --list-sdks) -match '^10\.' )
$dotnetPath = if ($hasNet10) { $dotnetCommand.Source } else { Join-Path $projectRoot '.tools\dotnet\dotnet.exe' }
if (!(Test-Path -LiteralPath $dotnetPath)) {
    $toolDirectory = Join-Path $projectRoot '.tools'
    New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
    $installer = Join-Path $toolDirectory 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & $installer -Channel 10.0 -InstallDir (Join-Path $toolDirectory 'dotnet') -NoPath
    if ($LASTEXITCODE -ne 0) { throw 'Could not install the local .NET SDK.' }
}
$appProject = Join-Path $projectRoot 'src\Unread.App\Unread.App.csproj'
[string[]]$restoreArguments = if ($NoRestore) { @('--no-restore') } else { @() }
if ($Task -eq 'Test') {
    & $dotnetPath test (Join-Path $projectRoot 'tests\Unread.Core.Tests\Unread.Core.Tests.csproj') @restoreArguments
} elseif ($Task -eq 'Publish') {
    $destination = Join-Path $projectRoot "artifacts\Unread-win-$($Architecture.ToLowerInvariant())"
    & $dotnetPath publish $appProject -c Release -r "win-$($Architecture.ToLowerInvariant())" "-p:Platform=$Architecture" "-p:Version=$Version" -o $destination @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Write-Host "Published to $destination"
} else {
    & $dotnetPath build $appProject "-p:Platform=$Architecture" -r "win-$($Architecture.ToLowerInvariant())" @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($Task -eq 'Run') {
        $executable = Join-Path $projectRoot "src\Unread.App\bin\$Architecture\Debug\net10.0-windows10.0.19041.0\win-$($Architecture.ToLowerInvariant())\Unread.App.exe"
        Start-Process -FilePath $executable
    }
}
if ($LASTEXITCODE -ne 0) { throw "$Task failed with exit code $LASTEXITCODE." }
