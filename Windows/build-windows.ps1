[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$windowsRoot = Split-Path -Parent $PSCommandPath
$projectRoot = Split-Path -Parent $windowsRoot
$project = Join-Path $windowsRoot "ConfigTool.Windows/ConfigTool.Windows.csproj"
$output = Join-Path ([System.IO.Path]::GetTempPath()) "PairPair-ConfigTool-$Runtime-$([Guid]::NewGuid().ToString('N'))"
$executable = Join-Path $projectRoot "PairPair ConfigTool.exe"

dotnet publish $project `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishDocumentationFiles=false `
    --output $output

Copy-Item (Join-Path $output "PairPair ConfigTool.exe") $executable -Force

Write-Host "Windows release: $executable"
Write-Host "Keep the root Resources folder beside the .exe when distributing it."
