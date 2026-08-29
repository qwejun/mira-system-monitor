$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $root
$dotnet = Join-Path $projectRoot ".tools\dotnet\dotnet.exe"
$project = Join-Path $root "MiraSystemMonitorAgent.csproj"
$output = Join-Path $root "dist"

if (-not (Test-Path $dotnet)) {
    throw "Project-local .NET 8 SDK was not found: $dotnet"
}

$env:DOTNET_CLI_HOME = Join-Path $projectRoot ".dotnet-home"
$env:NUGET_PACKAGES = Join-Path $projectRoot ".nuget-packages"

& $dotnet restore $project -r win-x64
if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed." }

& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $output `
    --no-restore
if ($LASTEXITCODE -ne 0) { throw "EXE publish failed." }

Write-Host "Generated: $(Join-Path $output 'MiraSystemMonitorAgent.exe')"
