param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$projectPath = Join-Path $repositoryRoot "LivestreamStudio.csproj"
$publishDirectory = Join-Path $repositoryRoot "artifacts\windows\publish"
$installerDirectory = Join-Path $repositoryRoot "artifacts\installers"
$scriptPath = Join-Path $PSScriptRoot "MosaicStudio.iss"
$programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use the numeric major.minor.patch form, such as 1.0.0."
}

$compilerCandidates = @(
    (Join-Path $programFilesX86 "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

if (-not $compilerCandidates) {
    throw "Inno Setup 6 is required. Install it from https://jrsoftware.org/isinfo.php, then run this script again."
}
$compilerPath = $compilerCandidates | Select-Object -First 1

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null

& dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "The self-contained Windows app publish failed (exit $LASTEXITCODE)."
}

$executablePath = Join-Path $publishDirectory "LivestreamStudio.exe"
if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "Publish succeeded but $executablePath is missing."
}

& $compilerPath `
    "/DAppVersion=$Version" `
    "/DPublishDir=$publishDirectory" `
    "/O$installerDirectory" `
    $scriptPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed to build the Windows installer (exit $LASTEXITCODE)."
}

$installerPath = Join-Path $installerDirectory "MosaicStudio-Windows-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Inno Setup completed but $installerPath was not created."
}

Write-Output "Windows installer created: $installerPath"
