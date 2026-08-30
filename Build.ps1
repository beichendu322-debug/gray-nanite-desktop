$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$output = Join-Path $root 'publish'
dotnet publish (Join-Path $root 'StellarisUI.csproj') -c Release -r win-x64 --self-contained true -o $output -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
Write-Host "Published to $output" -ForegroundColor Green
