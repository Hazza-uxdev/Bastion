param(
    [string]$Version = "2.9.25",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$releaseRoot = Join-Path $root "release"
$publishDir = Join-Path $releaseRoot "Bastion-v$Version-$Runtime"
$zipPath = Join-Path $releaseRoot "Bastion-v$Version-$Runtime.zip"
$msiPath = Join-Path $releaseRoot "Bastion-v$Version-$Runtime.msi"
$exeOutputDir = Join-Path $releaseRoot "installer-exe"

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
if (Test-Path -LiteralPath $exeOutputDir) { Remove-Item -LiteralPath $exeOutputDir -Recurse -Force }

dotnet publish (Join-Path $root "Bastion.csproj") `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir

Copy-Item -LiteralPath (Join-Path $root "Bastion.ico") -Destination $publishDir -Force
Copy-Item -LiteralPath (Join-Path $root "BrowserExtension") -Destination (Join-Path $publishDir "BrowserExtension") -Recurse -Force

if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

$wixCommand = Get-Command "wix.exe" -ErrorAction SilentlyContinue
$wix = if ($wixCommand) { $wixCommand.Source } else { $null }
if ($wix) {
    & $wix build (Join-Path $PSScriptRoot "Bastion.wxs") `
        -arch x64 `
        -d "PublishDir=$publishDir" `
        -d "Version=$Version" `
        -out $msiPath
} else {
    Write-Warning "WiX was not found. Install WiX Toolset and rerun this script to build the .msi."
}

$isccCommand = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
$iscc = if ($isccCommand) { $isccCommand.Source } else { $null }
if (-not $iscc) {
    $defaultInno = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (Test-Path -LiteralPath $defaultInno) {
        $iscc = $defaultInno
    }
}

if ($iscc) {
    New-Item -ItemType Directory -Path $exeOutputDir -Force | Out-Null
    & $iscc `
        "/DMyAppVersion=$Version" `
        "/DPublishDir=$publishDir" `
        "/DOutputDir=$exeOutputDir" `
        (Join-Path $PSScriptRoot "Bastion.iss")
} else {
    Write-Warning "Inno Setup was not found. Install Inno Setup 6 and rerun this script to build the .exe installer."
}

Write-Host ""
Write-Host "Release files:"
Write-Host "  $zipPath"
if (Test-Path -LiteralPath $msiPath) { Write-Host "  $msiPath" }
Get-ChildItem -LiteralPath $exeOutputDir -Filter "*.exe" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  $($_.FullName)"
}
