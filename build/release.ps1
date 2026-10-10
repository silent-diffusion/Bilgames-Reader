<#
.SYNOPSIS
  Builds Bilgames Reader and packages it as a one-click Windows installer (Setup.exe) with Velopack.

.EXAMPLE
  ./build/release.ps1 -Version 0.2.0
  ./build/release.ps1 -Version 0.2.0 -Publish      # also uploads a GitHub release (asks for confirmation)

.NOTES
  Output goes to ./artifacts/releases. Setup.exe installs per-user (no admin), adds a Start menu
  shortcut and an uninstall entry, and the app updates itself from GitHub Releases.
  Requires: .NET 8 SDK. The Velopack CLI (vpk) is installed as a local dotnet tool on first run.
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$Publish,
    [switch]$Yes,          # skip the upload confirmation (for unattended releases)
    [string]$Repo = "https://github.com/silent-diffusion/Bilgames-Reader"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "artifacts\publish"
$releaseDir = Join-Path $root "artifacts\releases"
$vpkVersion = "1.2.161"   # keep in step with the Velopack package in Bilgames.App.csproj

Push-Location $root
try {
    Write-Host "== Tests" -ForegroundColor Cyan
    dotnet test tests/Bilgames.Core.Tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }

    Write-Host "== Publish $Version" -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    dotnet publish src/Bilgames.App/Bilgames.App.csproj -c Release -r win-x64 --self-contained true `
        -p:Platform=x64 -p:Version=$Version -p:PublishReadyToRun=false -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

    Write-Host "== Velopack CLI" -ForegroundColor Cyan
    if (-not (Test-Path (Join-Path $root ".config\dotnet-tools.json"))) { dotnet new tool-manifest | Out-Null }
    dotnet tool install vpk --version $vpkVersion 2>$null | Out-Null
    dotnet tool restore | Out-Null

    Write-Host "== Pack installer" -ForegroundColor Cyan
    # A local-only build starts clean so the same version can be rebuilt.
    if (-not $Publish -and (Test-Path $releaseDir)) { Remove-Item -Recurse -Force $releaseDir }
    # Pull the previous release (if any) so vpk can build a small delta update.
    if ($Publish) { dotnet vpk download github --repoUrl $Repo -o $releaseDir 2>$null }
    dotnet vpk pack --packId BilgamesReader --packVersion $Version --packDir $publishDir `
        --mainExe BilgamesReader.exe --packTitle "Bilgames Reader" --packAuthors "Bilgames" `
        --icon src/Bilgames.App/Assets/AppIcon.ico -o $releaseDir
    if ($LASTEXITCODE -ne 0) { throw "Packing failed." }
    Write-Host "Installer: $(Join-Path $releaseDir 'BilgamesReader-win-Setup.exe')" -ForegroundColor Green

    if ($Publish) {
        if (-not $Yes) {
            $answer = Read-Host "Upload version $Version to $Repo as a GitHub release? (y/N)"
            if ($answer -ne "y") { Write-Host "Not uploaded."; return }
        }
        $token = (gh auth token)
        if (-not $token) { throw "Log in with 'gh auth login' first." }
        dotnet vpk upload github --repoUrl $Repo --token $token --publish --releaseName "Bilgames Reader $Version" --tag "v$Version" -o $releaseDir
        if ($LASTEXITCODE -ne 0) { throw "Upload failed." }
        Write-Host "Published v$Version." -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
