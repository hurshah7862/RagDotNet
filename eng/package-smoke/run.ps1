# Installs the packages in -Packages into the smoke app and runs it on net8.0 and net10.0.
# Usage: pwsh eng/package-smoke/run.ps1 -Packages artifacts/packages [-Frameworks net8.0,net10.0]
param(
    [Parameter(Mandatory)] [string] $Packages,
    [string[]] $Frameworks = @('net8.0', 'net10.0')
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here '../..')).Path

# Only the .nupkg files: symbol packages are not installed.
$feed = Join-Path $here 'packages'
Remove-Item -Recurse -Force $feed -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $feed | Out-Null
$nupkgs = Get-ChildItem (Resolve-Path $Packages) -Filter '*.nupkg'
if ($nupkgs.Count -eq 0) { throw "No .nupkg files in $Packages" }
$nupkgs | Copy-Item -Destination $feed
Write-Host "Installing $($nupkgs.Count) packages: $($nupkgs.Name -join ', ')"

# A fresh package cache, so a cached package with the same version can never stand in for the one just packed.
$env:NUGET_PACKAGES = Join-Path ([System.IO.Path]::GetTempPath()) "ragdotnet-smoke-nuget-$([guid]::NewGuid().ToString('N'))"

$project = Join-Path $here 'PackageSmoke.csproj'
dotnet restore $project
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$failed = @()
foreach ($tfm in $Frameworks) {
    Write-Host "::group::Smoke test ($tfm)"
    dotnet run --project $project --no-restore --configuration Release --framework $tfm -- $root
    $code = $LASTEXITCODE
    Write-Host '::endgroup::'
    if ($code -ne 0) { $failed += $tfm }
}

Remove-Item -Recurse -Force $env:NUGET_PACKAGES -ErrorAction SilentlyContinue
if ($failed.Count -gt 0) {
    Write-Host "Smoke test failed on: $($failed -join ', ')"
    exit 1
}
Write-Host "Smoke test passed on: $($Frameworks -join ', ')"
