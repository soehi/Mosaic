param(
    [string]$Output = "artifacts/linux-x64",
    [string]$Architecture = "linux-x64",
    [string]$Version = "",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src/Mosaic.Server/Mosaic.Server.csproj"
$outputPath = Join-Path $root $Output

if (Test-Path $outputPath) {
    Remove-Item -Recurse -Force $outputPath
}

$arguments = @(
    "publish", $project,
    "-c", "Release",
    "-r", $Architecture,
    "-o", $outputPath,
    "--self-contained", $(if ($FrameworkDependent) { "false" } else { "true" })
)

if ($Version -ne "") {
    $arguments += "-p:Version=$Version"
}

Write-Host "dotnet $($arguments -join ' ')"
& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码 $LASTEXITCODE"
}

Write-Host ""
Write-Host "已发布到 $outputPath"
Write-Host "服务器启动：MOSAIC_DATA_DIR=/var/lib/mosaic-server ./Mosaic.Server --urls http://0.0.0.0:8080"
