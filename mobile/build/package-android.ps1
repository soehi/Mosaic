param(
    [ValidateSet('apk', 'aab')]
    [string]$Format = 'aab',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'android-arm64'
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot '..\src\Mosaic.Mobile.App\Mosaic.Mobile.App.csproj'
if (-not (Test-Path $project)) {
    throw "Mobile app project not found: $project"
}

dotnet workload restore $project

$publishArgs = @(
    $project,
    '-f', 'net10.0-android',
    '-c', $Configuration,
    "-p:RuntimeIdentifier=$Runtime",
    "-p:AndroidPackageFormat=$Format"
)

if ($env:ANDROID_HOME) {
    $publishArgs += "-p:AndroidSdkDirectory=$env:ANDROID_HOME"
}

if ($env:JAVA_HOME) {
    $publishArgs += "-p:JavaSdkDirectory=$env:JAVA_HOME"
}

if ($env:MOSAIC_ANDROID_KEYSTORE) {
    if (-not $env:MOSAIC_ANDROID_KEYALIAS -or
        -not $env:MOSAIC_ANDROID_KEYPASS -or
        -not $env:MOSAIC_ANDROID_STOREPASS) {
        throw 'Set MOSAIC_ANDROID_KEYALIAS, MOSAIC_ANDROID_KEYPASS and MOSAIC_ANDROID_STOREPASS together with MOSAIC_ANDROID_KEYSTORE.'
    }

    $publishArgs += @(
        '-p:AndroidKeyStore=true',
        "-p:AndroidSigningKeyStore=$env:MOSAIC_ANDROID_KEYSTORE",
        "-p:AndroidSigningKeyAlias=$env:MOSAIC_ANDROID_KEYALIAS",
        "-p:AndroidSigningKeyPass=$env:MOSAIC_ANDROID_KEYPASS",
        "-p:AndroidSigningStorePass=$env:MOSAIC_ANDROID_STOREPASS"
    )
}

dotnet publish @publishArgs

$publishDir = Join-Path (Split-Path $project) "bin\$Configuration\net10.0-android\$Runtime\publish"
Write-Host "Publish output: $publishDir"