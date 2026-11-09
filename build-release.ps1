$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "src\CloudflareSmartProxy\IPOptimize.csproj"
$output = Join-Path $PSScriptRoot "artifacts\win-x64"

dotnet restore $project
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -o $output

Write-Host "发布完成：$output" -ForegroundColor Green
