param(
    [ValidateSet('apk', 'aab')]
    [string]$Format = 'apk',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'android-arm64',
    [string]$AndroidSdk = $env:ANDROID_HOME,
    [string]$JavaSdk = $env:JAVA_HOME,
    [switch]$RunTests
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Mosaic.Mobile.App\Mosaic.Mobile.App.csproj'
$tests = Join-Path $root 'tests\Mosaic.Mobile.Tests\Mosaic.Mobile.Tests.csproj'

if ($RunTests) {
    dotnet test $tests -c $Configuration --nologo
}

$publishArgs = @(
    $project,
    '-f', 'net10.0-android',
    '-c', $Configuration,
    "-p:RuntimeIdentifier=$Runtime",
    "-p:AndroidPackageFormat=$Format"
)

if ($AndroidSdk) { $publishArgs += "-p:AndroidSdkDirectory=$AndroidSdk" }
if ($JavaSdk) { $publishArgs += "-p:JavaSdkDirectory=$JavaSdk" }

dotnet publish @publishArgs
Write-Host "Artifacts: $root\src\Mosaic.Mobile.App\bin\$Configuration\net10.0-android"
